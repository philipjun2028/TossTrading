namespace TossTrading.Domain;

/// <summary>한국 표준시 유틸리티. 모든 장 시간 판단은 KST 기준.</summary>
public static class Kst
{
    public static readonly TimeZoneInfo Zone = FindZone();

    public static readonly TimeOnly MarketOpen = new(9, 0);
    public static readonly TimeOnly MarketClose = new(15, 30);

    public static DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone);

    public static DateTimeOffset ToKst(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Zone);

    public static DateTimeOffset At(DateOnly date, TimeOnly time) =>
        new(date.ToDateTime(time), Zone.GetUtcOffset(date.ToDateTime(time)));

    public static TimeOnly TimeOf(DateTimeOffset t) => TimeOnly.FromDateTime(ToKst(t).DateTime);

    public static DateOnly DateOf(DateTimeOffset t) => DateOnly.FromDateTime(ToKst(t).DateTime);

    /// <summary>분봉 시작 시각 (KST, 초 이하 버림)</summary>
    public static DateTimeOffset MinuteStart(DateTimeOffset t)
    {
        var k = ToKst(t);
        return new DateTimeOffset(k.Year, k.Month, k.Day, k.Hour, k.Minute, 0, k.Offset);
    }

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Asia/Seoul", "Korea Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("KST", TimeSpan.FromHours(9), "KST", "KST");
    }
}

/// <summary>시계 추상화. 실시간은 시스템 시계, 시뮬레이션은 가상 시계.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset Now => Kst.Now;
}
