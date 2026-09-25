namespace TossTrading.Domain;

public sealed record OrderRequest(
    string ClientOrderId,
    string Symbol,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    decimal? Price,
    OrderPriority Priority,
    string? BotId,
    string Reason)
{
    /// <summary>토스 clientOrderId 규칙: 1~36자, 영숫자/-/_ . GUID(N) = 32자.</summary>
    public static string NewClientOrderId() => Guid.NewGuid().ToString("N");
}

public sealed record OrderAck(string OrderId, string? ClientOrderId);

/// <summary>브로커가 보내는 주문 상태 스냅샷. FilledQuantity/AverageFilledPrice 는 해당 orderId 기준 누적값.</summary>
public sealed record OrderUpdate(
    string OrderId,
    string Symbol,
    OrderSide Side,
    OrderType Type,
    OrderStatus Status,
    decimal Quantity,
    decimal? Price,
    decimal FilledQuantity,
    decimal? AverageFilledPrice,
    DateTimeOffset Timestamp,
    string? Message = null);

public sealed record Holding(string Symbol, string Name, decimal Quantity, decimal AveragePrice, decimal LastPrice);

public sealed record AccountSnapshot(
    decimal Cash,
    IReadOnlyList<Holding> Holdings,
    IReadOnlyList<OrderUpdate> OpenOrders)
{
    public decimal HoldingsValue => Holdings.Sum(h => h.Quantity * h.LastPrice);
    public decimal Equity => Cash + HoldingsValue;
}

/// <summary>브로커 오류 (주문 거부 등). Code 는 토스 에러 코드 (예: insufficient-buying-power)</summary>
public class BrokerException : Exception
{
    public BrokerException(string code, string message, bool transient = false, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        IsTransient = transient;
        RetryAfter = retryAfter;
    }

    public string Code { get; }

    /// <summary>재시도해도 되는 오류 (네트워크, 429, 5xx)</summary>
    public bool IsTransient { get; }

    public TimeSpan? RetryAfter { get; }
}
