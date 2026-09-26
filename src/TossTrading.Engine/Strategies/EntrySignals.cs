using TossTrading.Domain;
using TossTrading.Engine.Market;

namespace TossTrading.Engine.Strategies;

public enum SignalTrigger { Trade, BarClosed }

/// <summary>진입 신호. StructuralStop 은 전략이 제안하는 손절가 (없으면 % 손절 사용).</summary>
/// <param name="SizeMultiplier">확신 등급에 따른 수량 배수 (1 = 기본, A등급이면 설정의 ConvictionMultiplier)</param>
public sealed record EntrySignal(decimal TriggerPrice, decimal? StructuralStop, string Reason, DateTimeOffset At, decimal SizeMultiplier = 1m);

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
        EntryStrategyKind.OvernightBasket => new OvernightBasketSignal(),
        _ => null,
    };

    public static string DisplayName(EntryStrategyKind kind) => kind switch
    {
        EntryStrategyKind.Manual => "수동",
        EntryStrategyKind.OpeningRangeBreakout => "ORB",
        EntryStrategyKind.VwapReclaim => "VWAP눌림",
        EntryStrategyKind.HighBreakout => "고가돌파",
        EntryStrategyKind.ClosingBet => "종가베팅",
        EntryStrategyKind.OvernightBasket => "오버나잇",
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
        if (rangePct > s.OrbMaxRangePct) return null;       // 범위가 너무 넓으면 돌파 후 밀림이 잦음

        // 등락률·갭 필터 (2026-01~09 연구: +3~10% 에서 돌파한 종목만 꾸준히 우위, 갭 10% 초과는 손실)
        if (ctx.PreviousClose is not > 0) return null;
        var changePct = (price / ctx.PreviousClose.Value - 1m) * 100m;
        if (changePct < s.OrbMinChangePct || changePct > s.OrbMaxChangePct) return null;
        var gapPct = ctx.DayOpen > 0 ? (ctx.DayOpen / ctx.PreviousClose.Value - 1m) * 100m : 0;
        if (gapPct > s.OrbMaxGapPct) return null;

        var avgVol = ctx.AverageBarVolume(10);
        var curVol = ctx.CurrentBar?.Volume ?? 0;
        var elapsed = Math.Max(0.1m, (decimal)(now - (ctx.CurrentBar?.Start ?? now)).TotalSeconds / 60m);
        var volRatio = avgVol > 0 ? curVol / Math.Min(elapsed, 1m) / avgVol : 0m;
        if (avgVol > 0 && volRatio < 1.2m) return null;     // 거래량 동반

        _brokenOn = today;
        // A등급: 범위 2~4%, 등락 +8% 이하, 장 시작 15분 안 돌파 (상·하반기 모두 거래당 +1.4% 이상)
        var minutes = (Kst.TimeOf(now) - Kst.MarketOpen).TotalMinutes;
        var gradeA = rangePct is >= 2m and <= 4m && changePct <= 8m && minutes <= 15;
        // 추세·거래량: 돌파 거래량 1.5배 이상 + 전일 종가가 20일선 위 (상·하반기 모두 +1.7% 이상)
        var trend = s.ConvictionTrendVolume && volRatio >= s.ConvictionVolumeRatio
                    && ctx.DailyMa20 is > 0 && ctx.PreviousClose.Value > ctx.DailyMa20.Value;
        var mult = (gradeA || trend) && s.ConvictionSizing ? s.ConvictionMultiplier : 1m;
        var grade = gradeA && trend ? " · A등급+추세" : gradeA ? " · A등급" : trend ? " · 추세·거래량" : "";
        return new EntrySignal(price, TickRules.AddTicks(lo, -1, ctx.Market),
            $"ORB{s.OrbMinutes} 고가 {hi:N0} 돌파 (범위 {rangePct:F1}%, 등락 {changePct:+0.0}%, 거래량 {volRatio:0.0}배){grade}", now, mult);
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

        // 추세형 필터 (프리셋 "VWAP 추세 눌림"): 거래량 동반 · 20일선 위 · 과열 아님
        if (s.VwapMaxChangePct > 0 && (ctx.ChangeRate ?? 0) * 100m >= s.VwapMaxChangePct) return null;
        if (s.VwapRequireAboveMa20 && !(ctx.DailyMa20 is > 0 && ctx.PreviousClose > ctx.DailyMa20)) return null;
        if (s.VwapMinVolumeRatio > 0)
        {
            decimal sum = 0; var n = 0;
            for (var i = 1; i <= 10 && ctx.ClosedBarFromEnd(i) is { } b; i++, n++) sum += b.Volume;
            if (n < 5 || b0.Volume < sum / n * s.VwapMinVolumeRatio) return null;
        }

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

/// <summary>
/// 오버나잇 바스켓: 진입 시간(기본 15:10~15:19)에 한 번 매수. 종목 선정은 자동 운용이 거래대금 순으로 한다.
/// 여기서는 매수하면 안 되는 경우만 거른다: 상한가 부근(체결이 어렵고 불리), 급등 후 저가권으로 밀린 종목.
/// </summary>
public sealed class OvernightBasketSignal : IEntrySignal
{
    private DateOnly _firedDate;

    public string Name => "오버나잇";

    public EntrySignal? Evaluate(SymbolContext ctx, DateTimeOffset now, BotSettings s, SignalTrigger trigger)
    {
        var today = Kst.DateOf(now);
        if (_firedDate == today) return null;
        var t = Kst.TimeOf(now);
        if (t < s.EntryStartTime || t >= s.EntryEndTime || t >= BotSettings.MarketCloseAuction) return null;
        var price = ctx.LastPrice;
        if (price <= 0 || ctx.ChangeRate is not { } change) return null;
        if (ctx.Limits?.Upper is { } upper && price >= upper * 0.985m) return null;       // 상한가 부근
        if (change * 100m >= 28m) return null;
        if (change * 100m > 8m && ctx.RangePosition is < 0.3m) return null;               // 급등 후 밀림
        _firedDate = today;
        return new EntrySignal(price, null, $"오버나잇: 등락 {change * 100m:+0.0}%, 범위 위치 {ctx.RangePosition:P0}", now);
    }
}
