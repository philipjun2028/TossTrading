namespace TossTrading.Engine.Backtest;

/// <summary>
/// 진행률(0~100)로 남은 시간을 추정한다. 단계마다 속도가 달라(첫 실행의 일봉 다운로드 vs 캐시된 재생)
/// 전체 평균이 아니라 최근 구간(기본 30초)의 속도로 계산한다.
/// </summary>
public sealed class EtaEstimator
{
    private readonly TimeSpan _window;
    private readonly Queue<(DateTime At, double Percent)> _samples = new();

    public EtaEstimator(TimeSpan? window = null) => _window = window ?? TimeSpan.FromSeconds(30);

    public DateTime Started { get; private set; }
    public TimeSpan Elapsed(DateTime now) => Started == default ? TimeSpan.Zero : now - Started;

    public void Reset(DateTime now)
    {
        _samples.Clear();
        Started = now;
    }

    /// <summary>현재 진행률을 기록하고 남은 시간 추정값을 돌려준다 (추정하기에 이르면 null).</summary>
    public TimeSpan? Update(DateTime now, double percent)
    {
        if (Started == default) Started = now;
        _samples.Enqueue((now, percent));
        while (_samples.Count > 2 && now - _samples.Peek().At > _window) _samples.Dequeue();

        if (percent >= 100) return TimeSpan.Zero;
        var (at0, p0) = _samples.Peek();
        var span = (now - at0).TotalSeconds;
        var gained = percent - p0;
        if (span < 3 || gained <= 0.2) return null;
        return TimeSpan.FromSeconds((100 - percent) / (gained / span));
    }

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}시간 {t.Minutes}분" :
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}분 {t.Seconds:00}초" : $"{Math.Max(0, t.Seconds)}초";
}
