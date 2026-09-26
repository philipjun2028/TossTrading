using TossTrading.Domain;
using TossTrading.Engine.Market;

namespace TossTrading.Engine.Strategies;

public enum SignalTrigger { Trade, BarClosed }

/// <summary>진입 신호. StructuralStop 은 전략이 제안하는 손절가 (없으면 % 손절 사용).</summary>
public sealed record EntrySignal(decimal TriggerPrice, decimal? StructuralStop, string Reason, DateTimeOffset At);

/// <summary>진입 전략 플러그인 (설계 문서 6.1). 전략 인스턴스는 봇 1개 전용이며 상태를 가질 수 있다.</summary>
public interface IEntrySignal
{
    string Name { get; }
    EntrySignal? Evaluate(SymbolContext ctx, DateTimeOffset now, BotSettings s, SignalTrigger trigger);
}

public static class EntrySignalFactory
{
    public static IEntrySignal? Create(EntryStrategyKind kind) => kind switch
    {
        EntryStrategyKind.OpeningRangeBreakout => new OpeningRangeBreakoutSignal(),
        EntryStrategyKind.VwapReclaim => new VwapReclaimSignal(),
        EntryStrategyKind.HighBreakout => new HighBreakoutSignal(),
        EntryStrategyKind.ClosingBet => new ClosingBetSignal(),
        _ => null,
    };

    public static string DisplayName(EntryStrategyKind kind) => kind switch
    {
        EntryStrategyKind.Manual => "수동",
        EntryStrategyKind.OpeningRangeBreakout => "ORB",
        EntryStrategyKind.VwapReclaim => "VWAP눌림",
        EntryStrategyKind.HighBreakout => "고가돌파",
        EntryStrategyKind.ClosingBet => "종가베팅",
        _ => kind.ToString(),
    };
}

/// <summary>
/// 시가범위 돌파 (ORB). 09:00~N분 고가를 "아래에서 위로" 돌파하는 순간만 신호 (추격 방지).
/// 조건: VWAP 위, 돌파 봉 거래량이 최근 평균 이상. 손절 = 범위 저가 − 1틱.
/// **하루 첫 돌파만** 신호: 한 번 돌파한 뒤 되밀렸다가 같은 가격을 다시 넘는 것은 돌파가 아니라 눌림 재돌파라
/// 백테스트에서 성과가 나빴다 (2026-01~09 토스 데이터).
/// </summary>
public sealed class OpeningRangeBreakoutSignal : IEntrySignal
{
    private decimal _prevPrice;
    private DateOnly _brokenOn;
    private decimal _maxInBar;
    private DateTimeOffset _maxInBarStart;

    public string Name => "ORB";

    public EntrySignal? Evaluate(SymbolContext ctx, DateTimeOffset now, BotSettings s, SignalTrigger trigger)
    {
        if (trigger != SignalTrigger.Trade) return null;
        var price = ctx.LastPrice;
        var prev = _prevPrice;
        _prevPrice = price;
        // 현재 분봉에서 이번 체결 전까지의 최고가 (같은 분 안에서 넘었다 내려온 경우 감지)
        var curStart = ctx.CurrentBar?.Start ?? default;
        if (curStart != _maxInBarStart) { _maxInBarStart = curStart; _maxInBar = 0; }
        var maxBeforeInBar = _maxInBar;
        _maxInBar = Math.Max(_maxInBar, price);

        var (hi, lo, complete) = ctx.OpeningRange(s.OrbMinutes, now);
        if (!complete || prev == 0) return null;

        var today = Kst.DateOf(now);
        if (_brokenOn == today) return null;                 // 오늘 이미 돌파함
        if (prev > hi || price <= hi) return null;          // 교차 순간만

        // 봇이 생기기 전에 이미 범위 위로 올라갔다 내려온 종목은 첫 돌파가 아님 (마감된 1분봉으로 확인)
        var rangeEnd = Kst.At(today, Kst.MarketOpen).AddMinutes(s.OrbMinutes);
        if (ctx.Bars.Any(b => b.Start >= rangeEnd && b.High > hi)) { _brokenOn = today; return null; }
        if (curStart >= rangeEnd && maxBeforeInBar > hi) { _brokenOn = today; return null; }
        if (price < ctx.Vwap) return null;
        var rangePct = lo > 0 ? (hi - lo) / lo * 100m : 0;
        if (rangePct > 10m) return null;                    // 범위가 너무 넓으면 손익비 불리

        var avgVol = ctx.AverageBarVolume(10);
        var curVol = ctx.CurrentBar?.Volume ?? 0;
        var elapsed = Math.Max(0.1m, (decimal)(now - (ctx.CurrentBar?.Start ?? now)).TotalSeconds / 60m);
        if (avgVol > 0 && curVol / Math.Min(elapsed, 1m) < avgVol * 1.2m) return null; // 거래량 동반

        _brokenOn = today;
        return new EntrySignal(price, TickRules.AddTicks(lo, -1, ctx.Market),
            $"ORB{s.OrbMinutes} 고가 {hi:N0} 돌파 (범위 {rangePct:F1}%)", now);
    }
}

/// <summary>
/// VWAP 눌림 재돌파. 상승 종목이 VWAP 부근까지 조정 후, 봉 마감 기준 VWAP 위 + 직전봉 고가 돌파.
/// 손절 = 최근 2봉 저가 − 1틱.
/// </summary>
public sealed class VwapReclaimSignal : IEntrySignal
{
    private DateTimeOffset _lastFire = DateTimeOffset.MinValue;

    public string Name => "VWAP 눌림";

    public EntrySignal? Evaluate(SymbolContext ctx, DateTimeOffset now, BotSettings s, SignalTrigger trigger)
    {
        if (trigger != SignalTrigger.BarClosed) return null;
        if (now - _lastFire < TimeSpan.FromMinutes(3)) return null;

        var b0 = ctx.ClosedBarFromEnd(0);
        var b1 = ctx.ClosedBarFromEnd(1);
        if (b0 is null || b1 is null) return null;

        var vwap = ctx.Vwap;
        if (vwap <= 0) return null;
        var uptrend = (ctx.ChangeRate ?? 0) >= 0.02m || ctx.DayHigh >= vwap * 1.02m;
        if (!uptrend) return null;

        var touched = Math.Min(b1.Low, b0.Low) <= vwap * 1.003m;
        var reclaimed = b0.Close > vwap && b0.Close > b1.High;
        var notExtended = b0.Close <= vwap * 1.02m;
        var strengthOk = ctx.Strength is null || ctx.Strength >= 100m;
        if (!(touched && reclaimed && notExtended && strengthOk)) return null;

        _lastFire = now;
        var stop = TickRules.AddTicks(Math.Min(b0.Low, b1.Low), -1, ctx.Market);
        return new EntrySignal(b0.Close, stop, $"VWAP {vwap:N0} 재돌파, 직전봉 고가 {b1.High:N0} 돌파", now);
    }
}

/// <summary>
/// 당일 고가 근처 박스(최근 10봉, 폭 ≤ 3%) 상단 돌파. 손절 = 박스 하단 − 1틱.
/// </summary>
public sealed class HighBreakoutSignal : IEntrySignal
{
    private const int BoxBars = 10;
    private decimal _prevPrice;
    private decimal _firedLevel;

    public string Name => "고가돌파";

    public EntrySignal? Evaluate(SymbolContext ctx, DateTimeOffset now, BotSettings s, SignalTrigger trigger)
    {
        if (trigger != SignalTrigger.Trade) return null;
        var price = ctx.LastPrice;
        var prev = _prevPrice;
        _prevPrice = price;
        if (prev == 0 || ctx.Bars.Count < BoxBars) return null;
        if ((ctx.ChangeRate ?? 0) < 0.02m) return null;

        decimal boxHigh = 0, boxLow = decimal.MaxValue;
        for (var i = 0; i < BoxBars; i++)
        {
            var b = ctx.ClosedBarFromEnd(i)!;
            boxHigh = Math.Max(boxHigh, b.High);
            boxLow = Math.Min(boxLow, b.Low);
        }
        if (boxHigh <= 0 || boxLow <= 0) return null;
        if ((boxHigh - boxLow) / boxLow > 0.03m) return null;       // 박스 폭
        if (boxHigh < ctx.DayHigh * 0.995m) return null;            // 당일 고가 근처 박스여야 함
        if (_firedLevel > 0 && boxHigh <= _firedLevel) return null; // 같은 레벨 재진입 금지
        if (prev > boxHigh || price <= boxHigh) return null;

        if (ctx.Limits?.Upper is { } upper && price >= upper * 0.97m) return null; // 상한가 근접 제외

        _firedLevel = boxHigh;
        return new EntrySignal(price, TickRules.AddTicks(boxLow, -1, ctx.Market),
            $"{BoxBars}분 박스 상단 {boxHigh:N0} 돌파", now);
    }
}

/// <summary>
/// 종가매매(종가 베팅). 진입 허용 시간(기본 15:00~15:19) 안에서 하루 한 번, 아래를 모두 만족하면 신호.
/// ① 당일 등락률이 하한~상한 사이 (강세지만 상한가 추격은 아님)
/// ② 당일 고저 범위의 상단에서 거래 (기본 75% 이상 = 고가 부근 마감)
/// ③ VWAP 위 (당일 매수자 평균 단가보다 위 = 수급 우위)
/// ④ 체결강도 100 이상 (데이터가 있을 때)
/// ⑤ 최근 30분 동안 밀리지 않음 (장 후반 매도세 회피)
/// ⑥ 상한가 3% 이내 제외 (익일 변동성·체결 불확실)
/// 손절은 % 기준 (구조적 손절 없음) — 익일 갭 하락은 손절가보다 더 밀려 체결될 수 있다.
/// </summary>
public sealed class ClosingBetSignal : IEntrySignal
{
    private DateOnly _firedDate;

    public string Name => "종가베팅";

    public EntrySignal? Evaluate(SymbolContext ctx, DateTimeOffset now, BotSettings s, SignalTrigger trigger)
    {
        var today = Kst.DateOf(now);
        if (_firedDate == today) return null;
        var t = Kst.TimeOf(now);
        if (t < s.EntryStartTime || t >= s.EntryEndTime || t >= BotSettings.MarketCloseAuction) return null;

        var price = ctx.LastPrice;
        if (price <= 0 || ctx.ChangeRate is not { } change) return null;
        var changePct = change * 100m;
        if (changePct < s.ClosingMinChangePct || changePct > s.ClosingMaxChangePct) return null;
        if (ctx.RangePosition is not { } pos || pos < s.ClosingMinRangePosition) return null;
        if (price < ctx.Vwap) return null;
        if (ctx.Strength is { } strength && strength < 100m) return null;
        if (ctx.Limits?.Upper is { } upper && price >= upper * 0.97m) return null;

        var thirtyMinAgo = ctx.Bars.LastOrDefault(b => b.Start <= now.AddMinutes(-30));
        if (thirtyMinAgo is not null && price < thirtyMinAgo.Close) return null;

        _firedDate = today;
        return new EntrySignal(price, null,
            $"종가베팅: 등락 {changePct:+0.0}%, 고저범위 {pos:P0} 위치, VWAP {ctx.Vwap:N0} 위", now);
    }
}
