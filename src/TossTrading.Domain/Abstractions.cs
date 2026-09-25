namespace TossTrading.Domain;

/// <summary>
/// 주문 실행 추상화. 실전(토스) / 모의(PaperBroker) 가 같은 인터페이스를 구현해
/// 엔진 코드는 모드와 무관하게 동일하게 동작한다.
/// </summary>
public interface IBroker : IAsyncDisposable
{
    string Name { get; }

    /// <summary>주문 상태 변경. 임의 스레드에서 호출될 수 있다.</summary>
    event Action<OrderUpdate>? OrderUpdated;

    Task StartAsync(CancellationToken ct);

    Task<OrderAck> PlaceOrderAsync(OrderRequest request, CancellationToken ct);

    /// <summary>정정. 새 주문 ID 를 돌려준다 (토스는 정정 시 새 orderId 발급).</summary>
    Task<OrderAck> ModifyOrderAsync(string orderId, OrderType type, decimal quantity, decimal? price, CancellationToken ct);

    Task CancelOrderAsync(string orderId, CancellationToken ct);

    Task<AccountSnapshot> GetAccountSnapshotAsync(CancellationToken ct);
}

/// <summary>실시간 시세 스트림 (토스 웹소켓 / 시뮬레이터 / 리플레이)</summary>
public interface IMarketDataFeed : IAsyncDisposable
{
    event Action<TradeTick>? Trade;
    event Action<OrderBookSnapshot>? OrderBook;
    event Action<bool, string>? ConnectionChanged;

    bool IsConnected { get; }

    Task StartAsync(CancellationToken ct);

    /// <summary>선언형 구독: 호출할 때마다 전체 구독 집합을 교체한다.</summary>
    Task SetSubscriptionsAsync(IReadOnlyCollection<string> tradeSymbols, IReadOnlyCollection<string> orderBookSymbols, CancellationToken ct);
}

/// <summary>REST 성격의 조회 (랭킹, 현재가, 분봉/일봉, 종목정보)</summary>
public interface IMarketDataSource
{
    Task<IReadOnlyList<RankingEntry>> GetRankingsAsync(RankingType type, int count, CancellationToken ct);

    Task<IReadOnlyList<PriceQuote>> GetPricesAsync(IReadOnlyList<string> symbols, CancellationToken ct);

    /// <summary>오늘 1분봉 (오래된 것부터 정렬)</summary>
    Task<IReadOnlyList<Bar>> GetTodayMinuteBarsAsync(string symbol, CancellationToken ct);

    /// <summary>최근 일봉 (오래된 것부터, 오늘 제외)</summary>
    Task<IReadOnlyList<Bar>> GetDailyBarsAsync(string symbol, int count, CancellationToken ct);

    Task<IReadOnlyList<StockInfo>> GetStocksAsync(IReadOnlyList<string> symbols, CancellationToken ct);

    Task<IReadOnlyList<StockWarning>> GetWarningsAsync(string symbol, CancellationToken ct);

    Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, CancellationToken ct);

    Task<PriceLimits?> GetPriceLimitsAsync(string symbol, CancellationToken ct);
}
