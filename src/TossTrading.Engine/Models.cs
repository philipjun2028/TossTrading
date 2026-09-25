using TossTrading.Domain;

namespace TossTrading.Engine;

public enum LogLevel { Info, Warn, Error, Trade }

public sealed record LogEntry(long Seq, DateTimeOffset Time, LogLevel Level, string Source, string Message);

/// <summary>진입~전량 청산 1사이클</summary>
public sealed record ClosedTrade(
    string BotId,
    string Symbol,
    string Name,
    string Strategy,
    ExecutionMode Mode,
    DateTimeOffset EntryTime,
    DateTimeOffset ExitTime,
    decimal Quantity,
    decimal AverageEntry,
    decimal AverageExit,
    decimal NetPnl,
    decimal NetPct,
    decimal? RMultiple,
    string EntryReason,
    string ExitReason);

public sealed record ScanCandidate(
    string Symbol,
    string Name,
    decimal Price,
    decimal ChangePct,
    decimal TradingAmount,
    decimal? Rvol,
    decimal? Strength,
    decimal TickCostPct,
    int? SpreadTicks,
    decimal? VwapDistPct,
    decimal? RangePosition,
    decimal Score,
    string Tags,
    decimal? Trend30mPct = null,
    string? ClosingChecks = null,
    int ClosingPassed = 0,
    int ClosingTotal = 0);

public sealed record BotView(
    string Id,
    string Symbol,
    string Name,
    string Strategy,
    BotMode Mode,
    BotState State,
    string StateText,
    decimal Quantity,
    decimal AveragePrice,
    decimal LastPrice,
    decimal? StopPrice,
    decimal UnrealizedNet,
    decimal UnrealizedPct,
    decimal RealizedNet,
    decimal TargetAmount,
    decimal TargetProgress,
    int Entries,
    int MaxEntries,
    int Wins,
    int Losses,
    string? PendingSignal,
    BotSettings Settings);

public sealed record AccountView(decimal Equity, decimal Cash, decimal StartEquity, decimal RealizedNet, decimal UnrealizedNet, decimal Exposure);

public sealed record RiskView(
    decimal DailyPnlPct,
    decimal DailyLossLimitPct,
    int OpenPositions,
    int MaxPositions,
    bool EntriesBlocked,
    string? BlockReason,
    bool KillSwitchActive);

public sealed record ChartLine(decimal Price, string Label, string Kind);

public sealed record ChartView(string Symbol, string Name, IReadOnlyList<Bar> Bars, IReadOnlyList<decimal> VwapSeries, IReadOnlyList<ChartLine> Lines);

public sealed record EngineSnapshot(
    DateTimeOffset Time,
    bool Running,
    bool FeedConnected,
    string Status,
    ExecutionMode Execution,
    DataSourceKind DataSource,
    AccountView Account,
    RiskView Risk,
    IReadOnlyList<BotView> Bots,
    IReadOnlyList<ScanCandidate> Candidates,
    IReadOnlyList<ClosedTrade> Trades,
    IReadOnlyList<LogEntry> Logs,
    ChartView? Chart)
{
    public static readonly EngineSnapshot Empty = new(
        DateTimeOffset.MinValue, false, false, "중지됨", ExecutionMode.Paper, DataSourceKind.Simulation,
        new AccountView(0, 0, 0, 0, 0, 0), new RiskView(0, 0, 0, 0, false, null, false),
        Array.Empty<BotView>(), Array.Empty<ScanCandidate>(), Array.Empty<ClosedTrade>(), Array.Empty<LogEntry>(), null);
}
