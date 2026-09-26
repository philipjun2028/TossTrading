using System.Collections.Concurrent;
using System.Globalization;
using TossTrading.Domain;

namespace TossTrading.Toss;

/// <summary>토스 DTO ↔ 도메인 모델 변환</summary>
public static class TossMapper
{
    public static OrderBookSnapshot ToOrderBook(string symbol, OrderbookDto b) => new(
        symbol,
        b.Timestamp ?? DateTimeOffset.Now,
        b.Asks.Select(l => new PriceLevel(l.Price, l.Volume)).ToList(),
        b.Bids.Select(l => new PriceLevel(l.Price, l.Volume)).ToList());

    public static Bar ToBar(CandleDto c) => new()
    {
        Start = c.Timestamp, Open = c.OpenPrice, High = c.HighPrice, Low = c.LowPrice, Close = c.ClosePrice, Volume = c.Volume,
        Value = (c.HighPrice + c.LowPrice + c.ClosePrice) / 3m * c.Volume,
    };

    public static OrderStatus ToStatus(string s) => s switch
    {
        "PENDING" => OrderStatus.Pending,
        "PARTIAL_FILLED" => OrderStatus.PartialFilled,
        "FILLED" => OrderStatus.Filled,
        "PENDING_CANCEL" => OrderStatus.PendingCancel,
        "PENDING_REPLACE" => OrderStatus.PendingReplace,
        "CANCELED" => OrderStatus.Canceled,
        "REJECTED" => OrderStatus.Rejected,
        "REPLACED" => OrderStatus.Replaced,
        "CANCEL_REJECTED" => OrderStatus.CancelRejected,
        "REPLACE_REJECTED" => OrderStatus.ReplaceRejected,
        _ => OrderStatus.Pending,
    };

    public static OrderUpdate ToUpdate(OrderDto o, string? message = null) => new(
        o.OrderId, o.Symbol,
        o.Side == "SELL" ? OrderSide.Sell : OrderSide.Buy,
        o.OrderType == "MARKET" ? OrderType.Market : OrderType.Limit,
        ToStatus(o.Status), o.Quantity, o.Price,
        o.Execution.FilledQuantity, o.Execution.AverageFilledPrice,
        o.Execution.FilledAt ?? o.CanceledAt ?? o.OrderedAt, message);

    public static string Num(decimal d) => d.ToString("0.########", CultureInfo.InvariantCulture);
}

/// <summary>REST 조회 → IMarketDataSource</summary>
public sealed class TossMarketDataSource : IMarketDataSource
{
    private readonly TossRestClient _rest;

    public TossMarketDataSource(TossRestClient rest) => _rest = rest;

    public async Task<IReadOnlyList<RankingEntry>> GetRankingsAsync(RankingType type, int count, CancellationToken ct)
    {
        var (t, duration) = type switch
        {
            RankingType.TradingAmount => ("MARKET_TRADING_AMOUNT", "realtime"),
            RankingType.TradingVolume => ("MARKET_TRADING_VOLUME", "realtime"),
            _ => ("TOP_GAINERS", "1d"), // 상승률은 realtime 미지원
        };
        var r = await _rest.GetRankingsAsync(t, "KR", duration, Math.Min(count, 100), excludeCaution: true, ct).ConfigureAwait(false);
        return r.Rankings.Select(i => new RankingEntry(i.Rank, i.Symbol, i.Price.LastPrice, i.Price.BasePrice, i.Price.ChangeRate, i.TradingVolume, i.TradingAmount)).ToList();
    }

    public async Task<IReadOnlyList<PriceQuote>> GetPricesAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var result = new List<PriceQuote>();
        foreach (var chunk in symbols.Chunk(200))
        {
            var list = await _rest.GetPricesAsync(chunk, ct).ConfigureAwait(false);
            result.AddRange(list.Select(p => new PriceQuote(p.Symbol, p.LastPrice, p.Timestamp)));
        }
        return result;
    }

    public async Task<IReadOnlyList<Bar>> GetTodayMinuteBarsAsync(string symbol, CancellationToken ct)
    {
        var today = Kst.DateOf(Kst.Now);
        var bars = new List<Bar>();
        DateTimeOffset? before = null;
        for (var page = 0; page < 3; page++) // 200 × 2 = 400분 > 정규장 390분
        {
            var p = await _rest.GetCandlesAsync(symbol, "1m", 200, before, ct).ConfigureAwait(false);
            var todays = p.Candles.Where(c => Kst.DateOf(c.Timestamp) == today).ToList();
            bars.AddRange(todays.Select(TossMapper.ToBar));
            if (p.NextBefore is null || todays.Count < p.Candles.Count || p.Candles.Count == 0) break;
            before = p.NextBefore;
        }
        return bars.OrderBy(b => b.Start).ToList();
    }

    public async Task<IReadOnlyList<Bar>> GetLatestSessionMinuteBarsAsync(string symbol, CancellationToken ct)
    {
        var bars = new List<Bar>();
        DateOnly? session = null;
        DateTimeOffset? before = null;
        for (var page = 0; page < 3; page++)
        {
            var p = await _rest.GetCandlesAsync(symbol, "1m", 200, before, ct).ConfigureAwait(false);
            if (p.Candles.Count == 0) break;
            session ??= Kst.DateOf(p.Candles.Max(c => c.Timestamp)); // 가장 최근 봉의 날짜 = 최근 거래일
            var sameDay = p.Candles.Where(c => Kst.DateOf(c.Timestamp) == session).ToList();
            bars.AddRange(sameDay.Select(TossMapper.ToBar));
            if (p.NextBefore is null || sameDay.Count < p.Candles.Count) break;
            before = p.NextBefore;
        }
        return bars.OrderBy(b => b.Start).ToList();
    }

    public async Task<IReadOnlyList<Bar>> GetDailyBarsAsync(string symbol, int count, CancellationToken ct)
    {
        var p = await _rest.GetCandlesAsync(symbol, "1d", Math.Min(count + 1, 200), null, ct).ConfigureAwait(false);
        var today = Kst.DateOf(Kst.Now);
        return p.Candles.Where(c => Kst.DateOf(c.Timestamp) < today).Select(TossMapper.ToBar)
            .OrderBy(b => b.Start).TakeLast(count).ToList();
    }

    /// <summary>
    /// 특정 거래일의 1분봉 (백테스트). before = 다음날 0시부터 과거로 페이지를 넘기며 그날 봉만 모은다.
    /// (NXT 시간외 봉이 섞여 있을 수 있어 최대 6페이지까지 본다. 정규장 필터는 재생기가 한다)
    /// </summary>
    public async Task<IReadOnlyList<Bar>> GetMinuteBarsForDateAsync(string symbol, DateOnly date, CancellationToken ct)
    {
        var bars = new List<Bar>();
        DateTimeOffset? before = Kst.At(date.AddDays(1), TimeOnly.MinValue);
        for (var page = 0; page < 6 && before is not null; page++)
        {
            var p = await _rest.GetCandlesAsync(symbol, "1m", 200, before, ct).ConfigureAwait(false);
            if (p.Candles.Count == 0) break;
            bars.AddRange(p.Candles.Where(c => Kst.DateOf(c.Timestamp) == date).Select(TossMapper.ToBar));
            var oldest = p.Candles.Min(c => c.Timestamp);
            if (Kst.DateOf(oldest) < date) break;        // 그날 09:00 이전까지 다 받음
            if (p.NextBefore is null || p.NextBefore >= before) break; // 더 과거가 없음 / 서버가 before 를 무시
            before = p.NextBefore;
        }
        return bars.GroupBy(b => b.Start).Select(g => g.First()).OrderBy(b => b.Start).ToList();
    }

    /// <summary>to 일자까지(포함) 일봉 최근 count 개 (백테스트)</summary>
    public async Task<IReadOnlyList<Bar>> GetDailyBarsUntilAsync(string symbol, DateOnly to, int count, CancellationToken ct)
    {
        var bars = new List<Bar>();
        DateTimeOffset? before = Kst.At(to.AddDays(1), TimeOnly.MinValue);
        for (var page = 0; page < 5 && bars.Count < count && before is not null; page++)
        {
            var p = await _rest.GetCandlesAsync(symbol, "1d", 200, before, ct).ConfigureAwait(false);
            if (p.Candles.Count == 0) break;
            bars.AddRange(p.Candles.Where(c => Kst.DateOf(c.Timestamp) <= to).Select(TossMapper.ToBar));
            if (p.NextBefore is null || p.NextBefore >= before) break;
            before = p.NextBefore;
        }
        return bars.GroupBy(b => Kst.DateOf(b.Start)).Select(g => g.First()).OrderBy(b => b.Start).TakeLast(count).ToList();
    }

    public async Task<IReadOnlyList<StockInfo>> GetStocksAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        var result = new List<StockInfo>();
        foreach (var chunk in symbols.Chunk(200))
        {
            var list = await _rest.GetStocksAsync(chunk, ct).ConfigureAwait(false);
            result.AddRange(list.Select(s => new StockInfo(
                s.Symbol, s.Name, s.Market ?? "", s.SecurityType ?? "", s.IsCommonShare,
                s.KoreanMarketDetail is { } k && (k.KrxTradingSuspended || s.Status is "DELISTED"),
                s.KoreanMarketDetail?.LiquidationTrading ?? false)));
        }
        return result;
    }

    public async Task<IReadOnlyList<StockWarning>> GetWarningsAsync(string symbol, CancellationToken ct)
    {
        var list = await _rest.GetWarningsAsync(symbol, ct).ConfigureAwait(false);
        var today = Kst.DateOf(Kst.Now);
        return list.Where(w => w.EndDate is null || w.EndDate >= today)
            .Select(w => new StockWarning(w.WarningType, w.StartDate, w.EndDate)).ToList();
    }

    public async Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, CancellationToken ct)
    {
        var b = await _rest.GetOrderbookAsync(symbol, ct).ConfigureAwait(false);
        return b.Asks.Count == 0 && b.Bids.Count == 0 ? null : TossMapper.ToOrderBook(symbol, b);
    }

    public async Task<PriceLimits?> GetPriceLimitsAsync(string symbol, CancellationToken ct)
    {
        var l = await _rest.GetPriceLimitsAsync(symbol, ct).ConfigureAwait(false);
        return new PriceLimits(l.UpperLimitPrice, l.LowerLimitPrice);
    }
}

/// <summary>웹소켓 시세 → IMarketDataFeed</summary>
public sealed class TossMarketFeed : IMarketDataFeed
{
    private readonly TossStreamClient _stream;
    private readonly bool _ownsStream;

    public TossMarketFeed(TossStreamClient stream, bool ownsStream = false)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        _stream.TradeReceived += t => Trade?.Invoke(t);
        _stream.OrderBookReceived += b => OrderBook?.Invoke(b);
        _stream.ConnectionChanged += (c, m) => ConnectionChanged?.Invoke(c, m);
    }

    public event Action<TradeTick>? Trade;
    public event Action<OrderBookSnapshot>? OrderBook;
    public event Action<bool, string>? ConnectionChanged;

    public bool IsConnected => _stream.IsConnected;

    public Task StartAsync(CancellationToken ct) => _stream.StartAsync(ct);

    public Task SetSubscriptionsAsync(IReadOnlyCollection<string> tradeSymbols, IReadOnlyCollection<string> orderBookSymbols, CancellationToken ct) =>
        _stream.SetMarketSubscriptionsAsync(tradeSymbols, orderBookSymbols, ct);

    public ValueTask DisposeAsync() => _ownsStream ? _stream.DisposeAsync() : ValueTask.CompletedTask;
}

/// <summary>
/// 실전 주문 → IBroker. 주문 이벤트는 웹소켓 personal:order 로 받고,
/// 이벤트 누락에 대비해 미체결 주문을 주기적으로 REST 로 확인한다 (워치독).
/// </summary>
public sealed class TossBroker : IBroker
{
    private readonly TossRestClient _rest;
    private readonly TossStreamClient _stream;
    private readonly ConcurrentDictionary<string, (DateTimeOffset LastEvent, string Status, decimal Filled)> _watch = new();
    private CancellationTokenSource? _cts;
    private Task? _watchdog;

    public TossBroker(TossRestClient rest, TossStreamClient stream)
    {
        _rest = rest;
        _stream = stream;
        _stream.OrderEventReceived += (evt, dto) => Publish(TossMapper.ToUpdate(dto, evt));
    }

    public string Name => "토스증권(실전)";

    public event Action<OrderUpdate>? OrderUpdated;

    public async Task StartAsync(CancellationToken ct)
    {
        if (_rest.Options.AccountSeq <= 0)
        {
            var accounts = await _rest.GetAccountsAsync(ct).ConfigureAwait(false);
            var acc = accounts.FirstOrDefault(a => a.AccountType == "BROKERAGE") ?? accounts.FirstOrDefault()
                      ?? throw new BrokerException("account-not-found", "토스증권 계좌를 찾을 수 없습니다.");
            _rest.Options.AccountSeq = acc.AccountSeq;
        }
        await _stream.SetOrderSubscriptionAsync(_rest.Options.AccountSeq, ct).ConfigureAwait(false);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _watchdog = Task.Run(() => WatchdogAsync(_cts.Token));
    }

    public async Task<OrderAck> PlaceOrderAsync(OrderRequest r, CancellationToken ct)
    {
        var body = new PlaceOrderBody
        {
            Symbol = r.Symbol,
            Side = r.Side == OrderSide.Buy ? "BUY" : "SELL",
            OrderType = r.Type == OrderType.Limit ? "LIMIT" : "MARKET",
            Quantity = TossMapper.Num(r.Quantity),
            Price = r.Type == OrderType.Limit && r.Price is { } p ? TossMapper.Num(p) : null,
            TimeInForce = "DAY",
            ClientOrderId = r.ClientOrderId,
        };
        var res = await _rest.PlaceOrderAsync(body, ct).ConfigureAwait(false);
        _watch[res.OrderId] = (DateTimeOffset.UtcNow, "PENDING", 0);
        return new OrderAck(res.OrderId, res.ClientOrderId ?? r.ClientOrderId);
    }

    public async Task<OrderAck> ModifyOrderAsync(string orderId, OrderType type, decimal quantity, decimal? price, CancellationToken ct)
    {
        var body = new ModifyOrderBody
        {
            OrderType = type == OrderType.Limit ? "LIMIT" : "MARKET",
            Quantity = quantity > 0 ? TossMapper.Num(quantity) : null,
            Price = type == OrderType.Limit && price is { } p ? TossMapper.Num(p) : null,
        };
        var res = await _rest.ModifyOrderAsync(orderId, body, ct).ConfigureAwait(false);
        _watch[res.OrderId] = (DateTimeOffset.UtcNow, "PENDING", 0);
        return new OrderAck(res.OrderId, null);
    }

    public async Task CancelOrderAsync(string orderId, CancellationToken ct)
    {
        await _rest.CancelOrderAsync(orderId, ct).ConfigureAwait(false);
        _watch.AddOrUpdate(orderId, _ => (DateTimeOffset.UtcNow, "PENDING_CANCEL", 0), (_, v) => v with { LastEvent = DateTimeOffset.UtcNow });
    }

    public async Task<AccountSnapshot> GetAccountSnapshotAsync(CancellationToken ct)
    {
        var bp = await _rest.GetBuyingPowerAsync("KRW", ct).ConfigureAwait(false);
        var holdings = await _rest.GetHoldingsAsync(ct).ConfigureAwait(false);
        var open = await _rest.ListOrdersAsync("OPEN", ct).ConfigureAwait(false);
        return new AccountSnapshot(
            bp.CashBuyingPower,
            holdings.Items.Where(h => h.MarketCountry is null or "KR")
                .Select(h => new Holding(h.Symbol, h.Name, h.Quantity, h.AveragePurchasePrice, h.LastPrice)).ToList(),
            open.Orders.Select(o => TossMapper.ToUpdate(o)).ToList());
    }

    private void Publish(OrderUpdate u)
    {
        if (u.Status.IsTerminal()) _watch.TryRemove(u.OrderId, out _);
        else _watch[u.OrderId] = (DateTimeOffset.UtcNow, u.Status.ToString(), u.FilledQuantity);
        OrderUpdated?.Invoke(u);
    }

    /// <summary>3초 이상 이벤트가 없는 미체결 주문을 REST 로 확인 (웹소켓 누락 대비)</summary>
    private async Task WatchdogAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var stale = _watch.Where(kv => DateTimeOffset.UtcNow - kv.Value.LastEvent > TimeSpan.FromSeconds(3))
                .OrderBy(kv => kv.Value.LastEvent).Take(2).ToList();
            foreach (var (orderId, prev) in stale)
            {
                try
                {
                    var dto = await _rest.GetOrderAsync(orderId, ct).ConfigureAwait(false);
                    var u = TossMapper.ToUpdate(dto, "REST 확인");
                    if (u.Status.IsTerminal() || u.FilledQuantity != prev.Filled || u.Status.ToString() != prev.Status) Publish(u);
                    else _watch[orderId] = prev with { LastEvent = DateTimeOffset.UtcNow };
                }
                catch (OperationCanceledException) { return; }
                catch (TossApiException ex) when (ex.Code == "order-not-found")
                {
                    _watch.TryRemove(orderId, out _);
                }
                catch
                {
                    _watch[orderId] = prev with { LastEvent = DateTimeOffset.UtcNow };
                }
            }
            // 오래된 항목 정리
            foreach (var kv in _watch.Where(kv => DateTimeOffset.UtcNow - kv.Value.LastEvent > TimeSpan.FromHours(8)).ToList())
                _watch.TryRemove(kv.Key, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_watchdog is not null)
        {
            try { await _watchdog.ConfigureAwait(false); } catch { }
        }
    }
}

/// <summary>REST + 웹소켓을 한 번에 만드는 편의 클래스</summary>
public sealed class TossConnection : IAsyncDisposable
{
    public TossConnection(TossOptions options)
    {
        Rest = new TossRestClient(options);
        Stream = new TossStreamClient(options, Rest.Tokens);
        Source = new TossMarketDataSource(Rest);
        Feed = new TossMarketFeed(Stream);
    }

    public TossRestClient Rest { get; }
    public TossStreamClient Stream { get; }
    public TossMarketDataSource Source { get; }
    public TossMarketFeed Feed { get; }

    public TossBroker CreateBroker() => new(Rest, Stream);

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
        Rest.Dispose();
    }
}

/// <summary>
/// 토스 과거 데이터 (백테스트). 과거 순위는 조회할 수 없어 현재 거래대금·거래량·상승률 상위 종목을 대상 풀로 쓴다.
/// 호출 한도는 TossRestClient 의 시세 그룹 제한을 따른다. CachedHistoryProvider 로 감싸서 쓰는 것을 권장.
/// </summary>
public sealed class TossHistoryProvider : IHistoryProvider
{
    private readonly TossMarketDataSource _source;

    public TossHistoryProvider(TossMarketDataSource source) => _source = source;

    public string Name => "토스 과거 데이터";

    public async Task<IReadOnlyList<StockInfo>> GetUniverseAsync(CancellationToken ct)
    {
        var symbols = new HashSet<string>();
        foreach (var type in new[] { RankingType.TradingAmount, RankingType.TradingVolume, RankingType.TopGainers })
            foreach (var e in await _source.GetRankingsAsync(type, 100, ct).ConfigureAwait(false))
                symbols.Add(e.Symbol);
        return symbols.Count == 0 ? Array.Empty<StockInfo>() : await _source.GetStocksAsync(symbols.ToList(), ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<Bar>> GetDailyBarsAsync(string symbol, DateOnly to, int count, CancellationToken ct) =>
        _source.GetDailyBarsUntilAsync(symbol, to, count, ct);

    public Task<IReadOnlyList<Bar>> GetMinuteBarsAsync(string symbol, DateOnly date, CancellationToken ct) =>
        _source.GetMinuteBarsForDateAsync(symbol, date, ct);
}
