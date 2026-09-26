using TossTrading.Domain;
using TossTrading.Engine.Market;

namespace TossTrading.Engine.Analytics;

/// <summary>
/// 특정 시점의 시장 상황 (진입·청산·신호 때 함께 기록 → "어떤 조건에서 진입한 거래가 좋았나" 분석용)
/// </summary>
public sealed record MarketSnapshot(
    DateTimeOffset Time,
    decimal Price,
    decimal? ChangePct,
    decimal Vwap,
    decimal? VwapDistPct,
    decimal? RangePosition,
    decimal DayHigh,
    decimal DayLow,
    decimal? Strength,
    decimal? AtrPct,
    decimal? VolumeRatio,
    int? SpreadTicks,
    decimal? BestBid,
    decimal? BestAsk,
    int MinutesFromOpen)
{
    public static MarketSnapshot From(SymbolContext ctx, DateTimeOffset now)
    {
        var price = ctx.LastPrice;
        var avgVol = ctx.AverageBarVolume(10);
        decimal? volRatio = null;
        if (avgVol > 0)
        {
            // 진행 중인 봉이 15초 이상 쌓였으면 1분 환산, 아니면(봉 마감 직후 신호 등) 직전 마감봉 기준
            var elapsedMin = ctx.CurrentBar is { } cur ? (decimal)(now - cur.Start).TotalMinutes : 0m;
            if (ctx.CurrentBar is { } bar && elapsedMin >= 0.25m)
                volRatio = Math.Round(bar.Volume / Math.Min(elapsedMin, 1m) / avgVol, 2);
            else if (ctx.LastClosedBar is { } last)
                volRatio = Math.Round(last.Volume / avgVol, 2);
        }
        var atr = ctx.Atr();
        return new MarketSnapshot(
            now, price,
            ctx.ChangeRate is { } c ? Math.Round(c * 100m, 2) : null,
            Math.Round(ctx.Vwap, 2),
            ctx.Vwap > 0 && price > 0 ? Math.Round((price / ctx.Vwap - 1m) * 100m, 2) : null,
            ctx.RangePosition is { } rp ? Math.Round(rp, 3) : null,
            ctx.DayHigh, ctx.DayLow,
            ctx.Strength is { } s ? Math.Round(s, 1) : null,
            atr is { } a && price > 0 ? Math.Round(a / price * 100m, 3) : null,
            volRatio,
            ctx.OrderBook?.SpreadTicks(ctx.Market),
            ctx.OrderBook?.BestBid, ctx.OrderBook?.BestAsk,
            (int)Math.Round((Kst.TimeOf(now) - Kst.MarketOpen).TotalMinutes));
    }
}

/// <summary>청산 사유 분류 (리포트에서 사유별 성과 비교용)</summary>
public enum ExitKind
{
    StopLoss,
    BreakEven,
    Trailing,
    TakeProfit,
    PartialTakeProfit,
    TimeStop,
    ForceClose,
    NextDayOpen,
    NextDayDeadline,
    Manual,
    KillSwitch,
    RiskLimit,
    Other,
}

/// <summary>매도 체결 1건. TriggerPrice = 매도를 결정한 기준가 (손절가·트레일링가·당시 현재가)</summary>
public sealed record ExitLeg(DateTimeOffset Time, decimal Quantity, decimal Price, decimal TriggerPrice, ExitKind Kind, string Reason);

/// <summary>
/// 거래 1건의 전체 분석 기록. 진입 근거·시장 상황·보유 중 최대 이익/손실(MFE/MAE)·청산 내역·설정값을 모두 담는다.
/// </summary>
public sealed record TradeAnalysisRecord(
    string TradeId,
    string BotId,
    string Symbol,
    string Name,
    string Strategy,
    BotMode BotMode,
    ExecutionMode Execution,
    DataSourceKind DataSource,
    DateTimeOffset SignalTime,
    decimal SignalPrice,
    decimal EntryLimitPrice,
    DateTimeOffset EntryTime,
    decimal AverageEntry,
    decimal Quantity,
    decimal InitialStop,
    decimal RiskPct,
    string EntryReason,
    MarketSnapshot? EntryContext,
    DateTimeOffset ExitTime,
    decimal AverageExit,
    IReadOnlyList<ExitLeg> Exits,
    ExitKind FinalExitKind,
    decimal GrossPnl,
    decimal Costs,
    decimal NetPnl,
    decimal NetPct,
    decimal? RMultiple,
    decimal MfePct,
    decimal MaePct,
    decimal EntrySlippagePct,
    decimal? ExitSlippagePct,
    double HoldMinutes,
    bool Overnight,
    BotSettings Settings,
    MarketSnapshot? ExitContext);

/// <summary>진입 신호 1건과 처리 결과 (진입/승인대기/차단/시간외 등). 진입하지 않은 신호도 기록해 필터 효과를 본다.</summary>
public sealed record SignalRecord(
    string SignalId,
    string BotId,
    string Symbol,
    string Name,
    string Strategy,
    BotMode BotMode,
    ExecutionMode Execution,
    DataSourceKind DataSource,
    DateTimeOffset Time,
    decimal Price,
    decimal? StructuralStop,
    string Reason,
    string Decision,
    string? DecisionDetail,
    MarketSnapshot Context);

/// <summary>반자동 신호의 이후 결정 (승인/거절/만료)</summary>
public sealed record SignalDecisionRecord(string SignalId, DateTimeOffset Time, string Decision, string? Detail);

/// <summary>
/// 기준 시점 이후 가격 추적 결과 (청산 후 / 신호 후). 절대 가격으로 저장하고 수익률은 리포트에서 계산한다.
/// EntryPrice 는 청산 추적일 때 원래 매수가 (손절 후 매수가 회복 여부 판단용).
/// </summary>
public sealed record FollowUpRecord(
    string RefId,
    string RefKind,
    string Symbol,
    DateTimeOffset StartTime,
    decimal StartPrice,
    decimal? EntryPrice,
    decimal? Price5m,
    decimal? Price15m,
    decimal? Price30m,
    decimal? Price60m,
    decimal? Max30m,
    decimal? Min30m,
    decimal? Max60m,
    decimal? Min60m,
    decimal? SessionClose,
    bool Complete);

/// <summary>진행 중인 거래의 분석용 누적 상태 (봇 상태와 함께 저장 → 익일 보유/재시작 후에도 이어서 기록)</summary>
public sealed class TradeTrack
{
    public string TradeId { get; set; } = "";
    public DateTimeOffset SignalTime { get; set; }
    public decimal SignalPrice { get; set; }
    public decimal EntryLimitPrice { get; set; }
    public MarketSnapshot? EntryContext { get; set; }
    public decimal MaxPrice { get; set; }
    public decimal MinPrice { get; set; }
    public List<ExitLeg> Exits { get; set; } = new();
    public ExitKind PendingExitKind { get; set; } = ExitKind.Other;
    public decimal PendingExitTrigger { get; set; }

    public void Observe(decimal price)
    {
        if (price <= 0) return;
        MaxPrice = MaxPrice == 0 ? price : Math.Max(MaxPrice, price);
        MinPrice = MinPrice == 0 ? price : Math.Min(MinPrice, price);
    }
}
