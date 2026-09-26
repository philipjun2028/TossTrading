using System.Text.Json;
using System.Text.Json.Serialization;

namespace TossTrading.Toss;

/// <summary>
/// 토스 Open API JSON 모델. 응답은 {"result": ...} 봉투, 오류는 {"error": {...}}.
/// 숫자(가격/수량)는 문자열로 온다 → AllowReadingFromString.
/// 필드명은 비공식 SDK(toss-go v0.3.0) 기준 — 공식 문서로 재확인 필요.
/// </summary>
public static class TossJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class ResultEnvelope<T>
{
    public T? Result { get; set; }
}

public sealed class ErrorEnvelope
{
    public ErrorBody? Error { get; set; }
}

public sealed class ErrorBody
{
    public string? RequestId { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
}

public sealed class TokenResponse
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
    [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    [JsonPropertyName("expires_in")] public long ExpiresIn { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
}

public sealed class AccountDto
{
    public string? AccountNo { get; set; }
    public long AccountSeq { get; set; }
    public string? AccountType { get; set; }
}

public sealed class PriceDto
{
    public string Symbol { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public decimal LastPrice { get; set; }
    public string? Currency { get; set; }
}

public sealed class LevelDto
{
    public decimal Price { get; set; }
    public decimal Volume { get; set; }
}

public sealed class OrderbookDto
{
    public DateTimeOffset? Timestamp { get; set; }
    public string? Currency { get; set; }
    public List<LevelDto> Asks { get; set; } = new();
    public List<LevelDto> Bids { get; set; } = new();
}

public sealed class CandleDto
{
    public DateTimeOffset Timestamp { get; set; }
    public decimal OpenPrice { get; set; }
    public decimal HighPrice { get; set; }
    public decimal LowPrice { get; set; }
    public decimal ClosePrice { get; set; }
    public decimal Volume { get; set; }
}

public sealed class CandlePageDto
{
    public List<CandleDto> Candles { get; set; } = new();
    public DateTimeOffset? NextBefore { get; set; }
}

public sealed class PriceLimitsDto
{
    public decimal? UpperLimitPrice { get; set; }
    public decimal? LowerLimitPrice { get; set; }
}

public sealed class RankingPriceDto
{
    public decimal LastPrice { get; set; }
    public decimal BasePrice { get; set; }
    public decimal? ChangeRate { get; set; }
}

public sealed class RankingItemDto
{
    public int Rank { get; set; }
    public string Symbol { get; set; } = "";
    public RankingPriceDto Price { get; set; } = new();
    public decimal TradingVolume { get; set; }
    public decimal TradingAmount { get; set; }
}

public sealed class RankingsDto
{
    public DateTimeOffset? RankedAt { get; set; }
    public List<RankingItemDto> Rankings { get; set; } = new();
}

public sealed class KoreanMarketDetailDto
{
    public bool LiquidationTrading { get; set; }
    public bool NxtSupported { get; set; }
    public bool KrxTradingSuspended { get; set; }
    public bool NxtTradingSuspended { get; set; }
}

public sealed class StockDto
{
    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Market { get; set; }
    public string? SecurityType { get; set; }
    public bool IsCommonShare { get; set; }
    public string? Status { get; set; }
    public KoreanMarketDetailDto? KoreanMarketDetail { get; set; }
}

public sealed class ListedStockDto
{
    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";
    public string? SecurityType { get; set; }
    public bool IsCommonShare { get; set; }
}

public sealed class WarningDto
{
    public string WarningType { get; set; } = "";
    public string? Exchange { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
}

public sealed class ExecutionDto
{
    public decimal FilledQuantity { get; set; }
    public decimal? AverageFilledPrice { get; set; }
    public decimal? FilledAmount { get; set; }
    public decimal? Commission { get; set; }
    public decimal? Tax { get; set; }
    public DateTimeOffset? FilledAt { get; set; }
}

public sealed class OrderDto
{
    public string OrderId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public string OrderType { get; set; } = "";
    public string? TimeInForce { get; set; }
    public string Status { get; set; } = "";
    public decimal? Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal? OrderAmount { get; set; }
    public DateTimeOffset OrderedAt { get; set; }
    public DateTimeOffset? CanceledAt { get; set; }
    public ExecutionDto Execution { get; set; } = new();
}

public sealed class OrderPageDto
{
    public List<OrderDto> Orders { get; set; } = new();
    public string? NextCursor { get; set; }
    public bool HasNext { get; set; }
}

public sealed class PlaceResultDto
{
    public string OrderId { get; set; } = "";
    public string? ClientOrderId { get; set; }
}

public sealed class BuyingPowerDto
{
    public string? Currency { get; set; }
    public decimal CashBuyingPower { get; set; }
}

public sealed class CommissionDto
{
    public string? MarketCountry { get; set; }
    public decimal CommissionRate { get; set; }
}

public sealed class HoldingMarketValueDto
{
    public decimal PurchaseAmount { get; set; }
    public decimal Amount { get; set; }
}

public sealed class HoldingItemDto
{
    public string Symbol { get; set; } = "";
    public string Name { get; set; } = "";
    public string? MarketCountry { get; set; }
    public decimal Quantity { get; set; }
    public decimal LastPrice { get; set; }
    public decimal AveragePurchasePrice { get; set; }
}

public sealed class HoldingsDto
{
    public List<HoldingItemDto> Items { get; set; } = new();
}

/// <summary>주문 생성 요청 본문 (숫자는 문자열로 전송)</summary>
public sealed class PlaceOrderBody
{
    public string Symbol { get; set; } = "";
    public string Side { get; set; } = "";
    public string OrderType { get; set; } = "";
    public string? Quantity { get; set; }
    public string? Price { get; set; }
    public string? TimeInForce { get; set; }
    public string? ClientOrderId { get; set; }
    public bool? ConfirmHighValueOrder { get; set; }
}

public sealed class ModifyOrderBody
{
    public string OrderType { get; set; } = "";
    public string? Quantity { get; set; }
    public string? Price { get; set; }
    public bool? ConfirmHighValueOrder { get; set; }
}

// ---------------------------------------------------------------- WebSocket

public sealed class WsFrame
{
    public string? Type { get; set; }
    public string? Id { get; set; }
    public string? Topic { get; set; }
    public JsonElement Data { get; set; }
    public List<string>? Subscribed { get; set; }
    public List<WsRejected>? Rejected { get; set; }
    public ErrorBody? Error { get; set; }
}

public sealed class WsRejected
{
    public string? Target { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
}

public sealed class WsTrade
{
    public decimal Price { get; set; }
    public decimal Volume { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

public sealed class WsOrderEvent
{
    public string? Event { get; set; }
    public string? AccountSeq { get; set; }
    public OrderDto? Order { get; set; }
}
