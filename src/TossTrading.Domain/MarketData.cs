namespace TossTrading.Domain;

public readonly record struct TradeTick(string Symbol, decimal Price, decimal Volume, DateTimeOffset Timestamp);

public sealed record PriceLevel(decimal Price, decimal Volume);

public sealed record OrderBookSnapshot(
    string Symbol,
    DateTimeOffset Timestamp,
    IReadOnlyList<PriceLevel> Asks,   // 낮은 가격부터
    IReadOnlyList<PriceLevel> Bids)   // 높은 가격부터
{
    public decimal? BestAsk => Asks.Count > 0 ? Asks[0].Price : null;
    public decimal? BestBid => Bids.Count > 0 ? Bids[0].Price : null;

    public int? SpreadTicks(MarketCountry market = MarketCountry.KR) =>
        BestAsk is { } a && BestBid is { } b ? TickRules.TicksBetween(b, a, market) : null;
}

/// <summary>OHLCV 봉. Value = Σ(가격×수량) → VWAP 계산용.</summary>
public sealed class Bar
{
    public DateTimeOffset Start { get; init; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public decimal Value { get; set; }

    public static Bar FromTrade(DateTimeOffset start, decimal price, decimal volume) => new()
    {
        Start = start, Open = price, High = price, Low = price, Close = price,
        Volume = volume, Value = price * volume,
    };

    public void Apply(decimal price, decimal volume)
    {
        if (price > High) High = price;
        if (price < Low) Low = price;
        Close = price;
        Volume += volume;
        Value += price * volume;
    }

    public decimal TypicalPrice => (High + Low + Close) / 3m;

    public Bar Clone() => (Bar)MemberwiseClone();
}

public sealed record RankingEntry(
    int Rank,
    string Symbol,
    decimal LastPrice,
    decimal BasePrice,
    decimal? ChangeRate,       // 비율 (0.0125 = 1.25%)
    decimal TradingVolume,
    decimal TradingAmount);

public sealed record StockInfo(
    string Symbol,
    string Name,
    string Market,
    string SecurityType,
    bool IsCommonShare,
    bool TradingSuspended,
    bool LiquidationTrading);

public sealed record StockWarning(string WarningType, DateOnly? StartDate, DateOnly? EndDate)
{
    public bool IsVi => WarningType.StartsWith("VI_", StringComparison.Ordinal);

    /// <summary>진입 금지 경고 (투자경고/위험/과열/정리매매)</summary>
    public bool IsBlocking => WarningType is "INVESTMENT_WARNING" or "INVESTMENT_RISK" or "OVERHEATED" or "LIQUIDATION_TRADING";
}

public sealed record PriceQuote(string Symbol, decimal LastPrice, DateTimeOffset? Timestamp);

public sealed record PriceLimits(decimal? Upper, decimal? Lower);
