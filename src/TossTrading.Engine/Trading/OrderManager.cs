using System.Collections.Concurrent;
using TossTrading.Domain;

namespace TossTrading.Engine.Trading;

public enum OrderJobKind { Place, Modify, Cancel }

public sealed record OrderJob(
    OrderJobKind Kind,
    string ClientOrderId,
    OrderPriority Priority,
    OrderRequest? Request = null,
    OrderType ModifyType = OrderType.Market,
    decimal ModifyQuantity = 0,
    decimal? ModifyPrice = null)
{
    public int Attempts { get; set; }
}

public sealed record OrderJobResult(OrderJob Job, OrderAck? Ack, string? Error, string? ErrorCode = null);

/// <summary>
/// 모든 봇이 공유하는 주문 큐 (설계 문서 7.5).
/// - 우선순위: 손절/킬스위치 &gt; 취소·정정 &gt; 익절 &gt; 신규 진입
/// - 호출 한도: RateGate (개장 직후 한도 축소 반영)
/// - 멱등성: 신규 주문 재시도 시 같은 clientOrderId 사용
/// </summary>
public sealed class OrderManager : IAsyncDisposable
{
    private readonly IBroker _broker;
    private readonly RateGate _gate;
    private readonly Action<OrderJobResult> _onResult;
    private readonly Action<LogLevel, string> _log;
    private readonly PriorityQueue<OrderJob, (int Priority, long Seq)> _queue = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentDictionary<string, string> _orderIds = new();       // clientOrderId → 현재 브로커 orderId
    private readonly ConcurrentDictionary<string, byte> _cancelBeforeSend = new();
    private readonly CancellationTokenSource _cts = new();
    private long _seq;
    private Task? _worker;

    public OrderManager(IBroker broker, Func<int> limitPerSecond, Action<OrderJobResult> onResult, Action<LogLevel, string> log)
    {
        _broker = broker;
        _gate = new RateGate(limitPerSecond);
        _onResult = onResult;
        _log = log;
    }

    public int QueueLength { get { lock (_lock) return _queue.Count; } }

    public void Start() => _worker ??= Task.Run(() => RunAsync(_cts.Token));

    public void Enqueue(OrderJob job)
    {
        lock (_lock) _queue.Enqueue(job, ((int)job.Priority, Interlocked.Increment(ref _seq)));
        _signal.Release();
    }

    /// <summary>브로커 orderId 매핑 (정정 시 새 ID 로 갱신)</summary>
    public void SetOrderId(string clientOrderId, string orderId) => _orderIds[clientOrderId] = orderId;

    public string? GetOrderId(string clientOrderId) => _orderIds.TryGetValue(clientOrderId, out var id) ? id : null;

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            OrderJob? job;
            lock (_lock) { if (!_queue.TryDequeue(out job, out _)) continue; }

            try
            {
                await ExecuteAsync(job, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log(LogLevel.Error, $"주문 처리 예외: {ex.Message}");
                _onResult(new OrderJobResult(job, null, ex.Message));
            }
        }
    }

    private async Task ExecuteAsync(OrderJob job, CancellationToken ct)
    {
        switch (job.Kind)
        {
            case OrderJobKind.Place:
                if (_cancelBeforeSend.TryRemove(job.ClientOrderId, out _))
                {
                    _onResult(new OrderJobResult(job, null, "전송 전 취소됨", "canceled-before-send"));
                    return;
                }
                await RunWithRetryAsync(job, async () =>
                {
                    var ack = await _broker.PlaceOrderAsync(job.Request!, ct).ConfigureAwait(false);
                    _orderIds[job.ClientOrderId] = ack.OrderId;
                    return ack;
                }, maxAttempts: 3, ct).ConfigureAwait(false);
                break;

            case OrderJobKind.Modify:
            case OrderJobKind.Cancel:
                var orderId = GetOrderId(job.ClientOrderId);
                if (orderId is null)
                {
                    if (job.Kind == OrderJobKind.Cancel && IsQueuedPlace(job.ClientOrderId))
                    {
                        _cancelBeforeSend[job.ClientOrderId] = 0;
                        return;
                    }
                    // 신규 주문 응답 대기 중 → 잠시 후 재시도
                    if (++job.Attempts > 20)
                    {
                        _onResult(new OrderJobResult(job, null, "대상 주문 ID 를 찾지 못함", "order-not-found"));
                        return;
                    }
                    _ = RequeueLaterAsync(job, TimeSpan.FromMilliseconds(250), ct);
                    return;
                }
                await RunWithRetryAsync(job, async () =>
                {
                    if (job.Kind == OrderJobKind.Cancel)
                    {
                        await _broker.CancelOrderAsync(orderId, ct).ConfigureAwait(false);
                        return new OrderAck(orderId, job.ClientOrderId);
                    }
                    var ack = await _broker.ModifyOrderAsync(orderId, job.ModifyType, job.ModifyQuantity, job.ModifyPrice, ct).ConfigureAwait(false);
                    _orderIds[job.ClientOrderId] = ack.OrderId;
                    return ack;
                }, maxAttempts: 3, ct).ConfigureAwait(false);
                break;
        }
    }

    private bool IsQueuedPlace(string clientOrderId)
    {
        lock (_lock) return _queue.UnorderedItems.Any(i => i.Element.Kind == OrderJobKind.Place && i.Element.ClientOrderId == clientOrderId);
    }

    private async Task RequeueLaterAsync(OrderJob job, TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct).ConfigureAwait(false); Enqueue(job); }
        catch (OperationCanceledException) { }
    }

    private async Task RunWithRetryAsync(OrderJob job, Func<Task<OrderAck>> action, int maxAttempts, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var ack = await action().ConfigureAwait(false);
                _onResult(new OrderJobResult(job, ack, null));
                return;
            }
            catch (BrokerException ex) when (ex.IsTransient && attempt < maxAttempts)
            {
                var wait = ex.RetryAfter ?? TimeSpan.FromMilliseconds(400 * attempt);
                _log(LogLevel.Warn, $"주문 재시도 {attempt}/{maxAttempts} ({ex.Code}) {wait.TotalMilliseconds:N0}ms 후");
                if (ex.RetryAfter is not null) await _gate.PenalizeAsync(wait, ct).ConfigureAwait(false);
                else await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            catch (BrokerException ex)
            {
                _onResult(new OrderJobResult(job, null, ex.Message, ex.Code));
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (attempt >= maxAttempts || job.Kind != OrderJobKind.Place)
                {
                    // 정정/취소는 멱등 키가 없어 네트워크 오류 시 자동 재시도하지 않는다 (결과는 주문 이벤트로 확인)
                    _onResult(new OrderJobResult(job, null, $"네트워크 오류: {ex.Message}", "network"));
                    return;
                }
                _log(LogLevel.Warn, $"네트워크 오류, 같은 clientOrderId 로 재전송 {attempt}/{maxAttempts}: {ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); } catch { /* 종료 중 */ }
        }
        _cts.Dispose();
    }
}
