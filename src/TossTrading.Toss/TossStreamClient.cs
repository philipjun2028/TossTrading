using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TossTrading.Domain;

namespace TossTrading.Toss;

/// <summary>
/// 토스 실시간 웹소켓 클라이언트 (wss://openapi-ws.tossinvest.com/ws/v1).
/// - 핸드셰이크: Authorization: Bearer 토큰
/// - 구독: 선언형. 전체 집합을 JSON 배열로 보낸다
///   [{"id":"7"},{"type":"trade:kr","codes":["005930"]},{"type":"orderbook:kr","codes":[...]},{"type":"personal:order","codes":["3"]}]
/// - 수신 프레임: {"type":"message","topic":"trade:kr:005930","data":{...}} / "subscriptions" / "error" / "pong"
/// - 핑: 텍스트 "PING" (60초), 서버는 180초 무응답 시 종료
/// - 재연결: 지수 백오프(1s→30s). 재연결 사이 누락된 주문 이벤트는 REST 로 재동기화해야 한다.
/// - 제한: 계정당 연결 2개, 연결당 토픽 100개, 선언 초당 5회
/// </summary>
public sealed class TossStreamClient : IAsyncDisposable
{
    public const int MaxTopics = 100;

    private readonly TossOptions _options;
    private readonly TossTokenProvider _tokens;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly RateGate _declareGate = new(4);
    private HashSet<string> _tradeCodes = new();
    private HashSet<string> _bookCodes = new();
    private long? _orderAccountSeq;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _declareSeq;
    private int _pendingDeclare;

    public TossStreamClient(TossOptions options, TossTokenProvider tokens)
    {
        _options = options;
        _tokens = tokens;
    }

    public event Action<TradeTick>? TradeReceived;
    public event Action<OrderBookSnapshot>? OrderBookReceived;
    public event Action<string, OrderDto>? OrderEventReceived;
    public event Action<bool, string>? ConnectionChanged;
    public event Action<string>? ErrorReceived;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public Task StartAsync(CancellationToken ct)
    {
        if (_loop is not null) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public Task SetMarketSubscriptionsAsync(IEnumerable<string> tradeSymbols, IEnumerable<string> bookSymbols, CancellationToken ct)
    {
        lock (_lock)
        {
            _tradeCodes = tradeSymbols.ToHashSet();
            _bookCodes = bookSymbols.ToHashSet();
        }
        return ScheduleDeclareAsync(ct);
    }

    public Task SetOrderSubscriptionAsync(long? accountSeq, CancellationToken ct)
    {
        lock (_lock) _orderAccountSeq = accountSeq;
        return ScheduleDeclareAsync(ct);
    }

    /// <summary>현재 구독 선언 JSON (테스트용 공개)</summary>
    public string BuildDeclaration(string? id)
    {
        lock (_lock)
        {
            var items = new List<object>();
            if (id is not null) items.Add(new Dictionary<string, string> { ["id"] = id });
            var budget = MaxTopics - (_orderAccountSeq is null ? 0 : 1);

            foreach (var (prefix, codes) in new[] { ("trade", _tradeCodes), ("orderbook", _bookCodes) })
            {
                foreach (var market in new[] { "kr", "us" })
                {
                    var list = codes.Where(c => MarketOf(c) == market).OrderBy(c => c, StringComparer.Ordinal).Take(Math.Max(0, budget)).ToList();
                    if (list.Count == 0) continue;
                    budget -= list.Count;
                    items.Add(new Dictionary<string, object> { ["type"] = $"{prefix}:{market}", ["codes"] = list });
                }
            }
            if (_orderAccountSeq is { } seq)
                items.Add(new Dictionary<string, object> { ["type"] = "personal:order", ["codes"] = new[] { seq.ToString(CultureInfo.InvariantCulture) } });
            if (items.Count == (id is null ? 0 : 1)) return "[]";
            return JsonSerializer.Serialize(items);
        }
    }

    /// <summary>국내 종목코드는 6자리 숫자 (일부 영문 포함 코드 대응: 앞 5자리 숫자)</summary>
    public static string MarketOf(string symbol) =>
        symbol.Length == 6 && symbol.Take(5).All(char.IsAsciiDigit) ? "kr" : "us";

    // 연속 변경을 150ms 모아서 한 번에 선언 (초당 5회 제한)
    private async Task ScheduleDeclareAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _pendingDeclare, 1) == 1) return;
        try { await Task.Delay(150, ct).ConfigureAwait(false); }
        finally { Interlocked.Exchange(ref _pendingDeclare, 0); }
        if (IsConnected) await DeclareAsync(ct).ConfigureAwait(false);
    }

    private async Task DeclareAsync(CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;
        await _declareGate.WaitAsync(ct).ConfigureAwait(false);
        var json = BuildDeclaration(Interlocked.Increment(ref _declareSeq).ToString(CultureInfo.InvariantCulture));
        await SendTextAsync(ws, json, ct).ConfigureAwait(false);
    }

    private async Task SendTextAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try { await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _sendLock.Release(); }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            attempt++;
            if (attempt > 1)
            {
                var delay = Math.Min(30, Math.Pow(2, Math.Min(attempt - 2, 5)));
                try { await Task.Delay(TimeSpan.FromSeconds(delay * (0.8 + Random.Shared.NextDouble() * 0.4)), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }

            using var ws = new ClientWebSocket();
            try
            {
                var token = await _tokens.GetTokenAsync(ct).ConfigureAwait(false);
                ws.Options.SetRequestHeader("Authorization", "Bearer " + token);
                ws.Options.KeepAliveInterval = TimeSpan.Zero; // 서버 규약대로 텍스트 PING 사용
                await ws.ConnectAsync(new Uri(_options.WebSocketUrl), ct).ConfigureAwait(false);
                _ws = ws;
                attempt = 0;
                ConnectionChanged?.Invoke(true, "토스 실시간 연결됨");
                await DeclareAsync(ct).ConfigureAwait(false);

                using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var ping = PingLoopAsync(ws, pingCts.Token);
                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
                pingCts.Cancel();
                try { await ping.ConfigureAwait(false); } catch { }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                ErrorReceived?.Invoke($"웹소켓 오류: {ex.Message}");
            }
            finally
            {
                _ws = null;
            }
            if (!ct.IsCancellationRequested) ConnectionChanged?.Invoke(false, "토스 실시간 연결 끊김 → 재연결 시도");
        }
    }

    private async Task PingLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            if (ws.State != WebSocketState.Open) break;
            await SendTextAsync(ws, "PING", ct).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            try { HandleFrame(text); }
            catch (Exception ex) { ErrorReceived?.Invoke($"프레임 처리 오류: {ex.Message}"); }
        }
    }

    /// <summary>수신 프레임 해석 (테스트용 공개)</summary>
    public void HandleFrame(string text)
    {
        if (text.Length == 0 || text[0] != '{') return; // "PONG" 등 텍스트 응답
        var frame = JsonSerializer.Deserialize<WsFrame>(text, TossJson.Options);
        if (frame is null) return;
        switch (frame.Type)
        {
            case "message":
                HandleMessage(frame);
                break;
            case "subscriptions":
                if (frame.Rejected is { Count: > 0 })
                    foreach (var r in frame.Rejected) ErrorReceived?.Invoke($"구독 거부 {r.Target}: {r.Code} {r.Message}");
                break;
            case "error":
                ErrorReceived?.Invoke($"구독 선언 오류: {frame.Error?.Code} {frame.Error?.Message}");
                break;
        }
    }

    private void HandleMessage(WsFrame frame)
    {
        var topic = frame.Topic ?? "";
        var idx = topic.LastIndexOf(':');
        if (idx <= 0) return;
        var type = topic[..idx];
        var code = topic[(idx + 1)..];

        if (type.StartsWith("trade:", StringComparison.Ordinal))
        {
            var t = frame.Data.Deserialize<WsTrade>(TossJson.Options);
            if (t is null || t.Price <= 0 || t.Volume <= 0) return;
            TradeReceived?.Invoke(new TradeTick(code, t.Price, t.Volume, t.Timestamp));
        }
        else if (type.StartsWith("orderbook:", StringComparison.Ordinal))
        {
            var b = frame.Data.Deserialize<OrderbookDto>(TossJson.Options);
            if (b is null) return;
            OrderBookReceived?.Invoke(TossMapper.ToOrderBook(code, b));
        }
        else if (type == "personal:order")
        {
            var e = frame.Data.Deserialize<WsOrderEvent>(TossJson.Options);
            if (e?.Order is null || string.IsNullOrEmpty(e.Order.OrderId)) return;
            OrderEventReceived?.Invoke(e.Event ?? "", e.Order);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        var ws = _ws;
        if (ws is { State: WebSocketState.Open })
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { }
        }
    }
}
