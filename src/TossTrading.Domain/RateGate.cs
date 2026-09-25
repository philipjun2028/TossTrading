namespace TossTrading.Domain;

/// <summary>
/// 1초 슬라이딩 윈도우 호출 제한기. 한도는 호출 시점마다 함수로 평가하므로
/// "09:00~09:10 은 초당 3회" 같은 시간대별 한도나 서버 헤더 기반 보정을 반영할 수 있다.
/// </summary>
public sealed class RateGate
{
    private readonly Func<int> _limitPerSecond;
    private readonly Queue<long> _stamps = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Func<long> _nowTicks;

    public RateGate(Func<int> limitPerSecond, Func<long>? nowTicks = null)
    {
        _limitPerSecond = limitPerSecond;
        _nowTicks = nowTicks ?? (() => Environment.TickCount64);
    }

    public RateGate(int limitPerSecond) : this(() => limitPerSecond) { }

    /// <summary>호출 권한을 얻을 때까지 대기한다.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                var limit = Math.Max(1, _limitPerSecond());
                var now = _nowTicks();
                while (_stamps.Count > 0 && now - _stamps.Peek() >= 1000) _stamps.Dequeue();
                if (_stamps.Count < limit)
                {
                    _stamps.Enqueue(now);
                    return;
                }
                var wait = 1000 - (now - _stamps.Peek()) + 5;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(wait, 5)), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <summary>서버가 429 를 준 경우 일정 시간 전체 호출을 막는다.</summary>
    public async Task PenalizeAsync(TimeSpan delay, CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try { await Task.Delay(delay, ct).ConfigureAwait(false); }
        finally { _mutex.Release(); }
    }
}
