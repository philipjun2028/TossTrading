using TossTrading.Domain;

namespace TossTrading.Engine.Paper;

/// <summary>
/// 모의 체결 브로커 (설계 문서 8.8). 실시간(또는 시뮬레이션) 시세를 그대로 쓰고 주문만 가상 체결한다.
/// - 공격적 지정가/시장가: 호가창 잔량을 걸어 올라가며 체결
/// - 대기 지정가: 체결가가 주문가를 "넘어설 때만" 체결 (같은 가격은 대기열 가정 → 보수적)
/// - 수수료·세금은 CostModel 로 동일하게 계산
/// OnTrade/OnOrderBook 은 엔진 루프에서, Place/Modify/Cancel 은 주문 큐 스레드에서 호출되므로 락으로 보호한다.
/// </summary>
public sealed class PaperBroker : IBroker
{
    private sealed class PaperOrder
    {
        public required string OrderId { get; init; }
        public required string Symbol { get; init; }
        public required OrderSide Side { get; init; }
        public required OrderType Type { get; set; }
        public required decimal Quantity { get; init; }
        public decimal? Price { get; set; }
        public decimal Filled { get; set; }
        public decimal FilledValue { get; set; }
        public OrderStatus Status { get; set; } = OrderStatus.Pending;
        public decimal Remaining => Quantity - Filled;
    }

    private sealed class Position
    {
        public decimal Quantity;
        public decimal AveragePrice;
    }

    private readonly object _lock = new();
    private readonly CostModel _cost;
    private readonly IClock _clock;
    private readonly Dictionary<string, PaperOrder> _orders = new();
    private readonly Dictionary<string, Position> _positions = new();
    private readonly Dictionary<string, OrderBookSnapshot> _books = new();
    private readonly Dictionary<string, decimal> _lastPrices = new();
    private readonly Dictionary<string, string> _names = new();
    private decimal _cash;
    private long _seq;

    public PaperBroker(decimal startingCash, CostModel cost, IClock clock)
    {
        _cash = startingCash;
        _cost = cost;
        _clock = clock;
    }

    public string Name => "모의(Paper)";

    public event Action<OrderUpdate>? OrderUpdated;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public void SetName(string symbol, string name)
    {
        lock (_lock) _names[symbol] = name;
    }

    // ------------------------------------------------------------ 시세 입력

    public void OnOrderBook(OrderBookSnapshot book)
    {
        List<OrderUpdate> updates;
        lock (_lock)
        {
            _books[book.Symbol] = book;
            updates = MatchRestingLocked(book.Symbol, tradePrice: null);
        }
        Publish(updates);
    }

    public void OnTrade(TradeTick tick)
    {
        List<OrderUpdate> updates;
        lock (_lock)
        {
            _lastPrices[tick.Symbol] = tick.Price;
            updates = MatchRestingLocked(tick.Symbol, tick.Price);
        }
        Publish(updates);
    }

    // ------------------------------------------------------------ IBroker

    public Task<OrderAck> PlaceOrderAsync(OrderRequest r, CancellationToken ct)
    {
        List<OrderUpdate> updates = new();
        string orderId;
        lock (_lock)
        {
            orderId = $"P{Interlocked.Increment(ref _seq):D8}";
            if (r.Quantity <= 0 || decimal.Truncate(r.Quantity) != r.Quantity)
                throw new BrokerException("invalid-request", "수량은 양의 정수여야 합니다.");
            if (r.Type == OrderType.Limit && (r.Price is null or <= 0))
                throw new BrokerException("invalid-request", "지정가 주문에는 가격이 필요합니다.");
            if (r.Price is { } lp && TickRules.RoundDown(lp) != lp)
                throw new BrokerException("price-out-of-range", $"호가단위에 맞지 않는 가격 {lp}");

            var refPrice = r.Price ?? BestPriceLocked(r.Symbol, r.Side) ?? 0;
            if (r.Side == OrderSide.Buy)
            {
                var need = refPrice * r.Quantity + _cost.BuyCost(refPrice, r.Quantity) + ReservedCashLocked();
                if (refPrice <= 0 || need > _cash)
                    throw new BrokerException("insufficient-buying-power", $"매수 가능 금액 부족 (필요 {need:N0}, 가능 {_cash:N0})");
            }
            else
            {
                var held = _positions.TryGetValue(r.Symbol, out var pos) ? pos.Quantity : 0;
                var reserved = _orders.Values.Where(o => o.Symbol == r.Symbol && o.Side == OrderSide.Sell && !o.Status.IsTerminal()).Sum(o => o.Remaining);
                if (r.Quantity > held - reserved)
                    throw new BrokerException("insufficient-sellable-quantity", $"매도 가능 수량 부족 (보유 {held}, 주문중 {reserved})");
            }

            var order = new PaperOrder
            {
                OrderId = orderId, Symbol = r.Symbol, Side = r.Side, Type = r.Type, Quantity = r.Quantity, Price = r.Price,
            };
            _orders[orderId] = order;
            updates.Add(Snapshot(order));
            updates.AddRange(TryMatchAggressiveLocked(order));
        }
        Publish(updates);
        return Task.FromResult(new OrderAck(orderId, r.ClientOrderId));
    }

    public Task<OrderAck> ModifyOrderAsync(string orderId, OrderType type, decimal quantity, decimal? price, CancellationToken ct)
    {
        List<OrderUpdate> updates = new();
        string newId;
        lock (_lock)
        {
            if (!_orders.TryGetValue(orderId, out var old)) throw new BrokerException("order-not-found", "주문 없음");
            if (old.Status.IsTerminal()) throw new BrokerException("already-filled", "이미 종료된 주문");
            old.Status = OrderStatus.Replaced;
            updates.Add(Snapshot(old));

            newId = $"P{Interlocked.Increment(ref _seq):D8}";
            var qty = Math.Min(quantity > 0 ? quantity : old.Remaining, old.Remaining);
            var order = new PaperOrder
            {
                OrderId = newId, Symbol = old.Symbol, Side = old.Side, Type = type, Quantity = qty,
                Price = type == OrderType.Limit ? price : null,
            };
            _orders[newId] = order;
            updates.Add(Snapshot(order));
            updates.AddRange(TryMatchAggressiveLocked(order));
        }
        Publish(updates);
        return Task.FromResult(new OrderAck(newId, null));
    }

    public Task CancelOrderAsync(string orderId, CancellationToken ct)
    {
        OrderUpdate? update = null;
        lock (_lock)
        {
            if (!_orders.TryGetValue(orderId, out var o)) throw new BrokerException("order-not-found", "주문 없음");
            if (o.Status.IsTerminal()) throw new BrokerException("already-filled", "이미 종료된 주문");
            o.Status = OrderStatus.Canceled;
            update = Snapshot(o);
        }
        Publish(new List<OrderUpdate> { update });
        return Task.CompletedTask;
    }

    public Task<AccountSnapshot> GetAccountSnapshotAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            var holdings = _positions.Where(p => p.Value.Quantity > 0)
                .Select(p => new Holding(p.Key, _names.GetValueOrDefault(p.Key, p.Key), p.Value.Quantity, p.Value.AveragePrice,
                    _lastPrices.GetValueOrDefault(p.Key, p.Value.AveragePrice)))
                .ToList();
            var open = _orders.Values.Where(o => !o.Status.IsTerminal()).Select(Snapshot).ToList();
            return Task.FromResult(new AccountSnapshot(_cash - ReservedCashLocked(), holdings, open));
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ------------------------------------------------------------ 매칭

    private decimal? BestPriceLocked(string symbol, OrderSide side)
    {
        if (_books.TryGetValue(symbol, out var b))
        {
            var p = side == OrderSide.Buy ? b.BestAsk : b.BestBid;
            if (p is not null) return p;
        }
        return _lastPrices.TryGetValue(symbol, out var last) ? last : null;
    }

    private decimal ReservedCashLocked() =>
        _orders.Values.Where(o => o.Side == OrderSide.Buy && !o.Status.IsTerminal())
            .Sum(o => (o.Price ?? BestPriceLocked(o.Symbol, OrderSide.Buy) ?? 0) * o.Remaining * (1 + _cost.CommissionRate));

    /// <summary>주문 접수 직후: 호가창과 교차하면 즉시 체결 (잔량만큼 걸어 올라감)</summary>
    private List<OrderUpdate> TryMatchAggressiveLocked(PaperOrder o)
    {
        var updates = new List<OrderUpdate>();
        _books.TryGetValue(o.Symbol, out var book);
        var levels = o.Side == OrderSide.Buy ? book?.Asks : book?.Bids;

        if (levels is { Count: > 0 })
        {
            foreach (var lv in levels)
            {
                if (o.Remaining <= 0) break;
                var crosses = o.Type == OrderType.Market
                              || (o.Side == OrderSide.Buy ? lv.Price <= o.Price : lv.Price >= o.Price);
                if (!crosses) break;
                var q = Math.Min(o.Remaining, Math.Max(lv.Volume, 1));
                ApplyFillLocked(o, q, lv.Price);
            }
            // 시장가인데 호가 잔량이 부족하면 마지막 호가에서 나머지 체결 (보수적 근사)
            if (o.Type == OrderType.Market && o.Remaining > 0)
                ApplyFillLocked(o, o.Remaining, levels[^1].Price);
        }
        else if (o.Type == OrderType.Market && _lastPrices.TryGetValue(o.Symbol, out var last))
        {
            var slip = TickRules.AddTicks(last, o.Side == OrderSide.Buy ? 1 : -1);
            ApplyFillLocked(o, o.Remaining, slip);
        }

        if (o.Filled > 0) updates.Add(Snapshot(o));
        return updates;
    }

    /// <summary>대기 지정가: 체결가가 주문가를 넘어서거나, 호가가 주문가까지 들어오면 체결</summary>
    private List<OrderUpdate> MatchRestingLocked(string symbol, decimal? tradePrice)
    {
        var updates = new List<OrderUpdate>();
        _books.TryGetValue(symbol, out var book);
        foreach (var o in _orders.Values)
        {
            if (o.Symbol != symbol || o.Status.IsTerminal() || o.Remaining <= 0) continue;
            if (o.Type == OrderType.Market)
            {
                var before = o.Filled;
                TryMatchAggressiveLocked(o);
                if (o.Filled > before) updates.Add(Snapshot(o));
                continue;
            }
            var limit = o.Price!.Value;
            decimal? fillPrice = null;
            if (o.Side == OrderSide.Buy)
            {
                if (tradePrice is { } tp && tp < limit) fillPrice = limit;
                else if (book?.BestAsk is { } ask && ask <= limit) fillPrice = ask;
            }
            else
            {
                if (tradePrice is { } tp && tp > limit) fillPrice = limit;
                else if (book?.BestBid is { } bid && bid >= limit) fillPrice = bid;
            }
            if (fillPrice is null) continue;
            ApplyFillLocked(o, o.Remaining, fillPrice.Value);
            updates.Add(Snapshot(o));
        }
        return updates;
    }

    private void ApplyFillLocked(PaperOrder o, decimal qty, decimal price)
    {
        if (qty <= 0) return;
        o.Filled += qty;
        o.FilledValue += qty * price;
        o.Status = o.Remaining <= 0 ? OrderStatus.Filled : OrderStatus.PartialFilled;

        if (!_positions.TryGetValue(o.Symbol, out var pos)) _positions[o.Symbol] = pos = new Position();
        if (o.Side == OrderSide.Buy)
        {
            _cash -= price * qty + _cost.BuyCost(price, qty);
            var newQty = pos.Quantity + qty;
            pos.AveragePrice = (pos.AveragePrice * pos.Quantity + price * qty) / newQty;
            pos.Quantity = newQty;
        }
        else
        {
            _cash += price * qty - _cost.SellCost(price, qty);
            pos.Quantity -= qty;
            if (pos.Quantity <= 0) { pos.Quantity = 0; pos.AveragePrice = 0; }
        }
    }

    private OrderUpdate Snapshot(PaperOrder o) => new(
        o.OrderId, o.Symbol, o.Side, o.Type, o.Status, o.Quantity, o.Price, o.Filled,
        o.Filled > 0 ? o.FilledValue / o.Filled : null, _clock.Now);

    private void Publish(List<OrderUpdate> updates)
    {
        foreach (var u in updates) OrderUpdated?.Invoke(u);
    }
}
