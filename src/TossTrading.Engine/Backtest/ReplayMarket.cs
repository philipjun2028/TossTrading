using TossTrading.Domain;

namespace TossTrading.Engine.Backtest;

/// <summary>
/// 과거 1분봉 재생기. 엔진에는 실시간 시세(IMarketDataFeed) + 조회(IMarketDataSource) + 시계(IClock)로 보인다.
///  - 1분봉 하나를 4개 체결로 쪼갠다: 양봉 시가→저가→고가→종가, 음봉 시가→고가→저가→종가 (보수적인 경로)
///  - 호가는 체결가 기준 1틱 스프레드로 합성한다
///  - 조회 결과는 모두 "지금 시각까지" 데이터만 돌려준다 (미래 데이터 사용 금지)
/// 백테스트 실행기(BacktestRunner)만 시계를 움직인다.
/// </summary>
public sealed class ReplayMarket : IMarketDataFeed, IMarketDataSource, IClock
{
    /// <summary>한 분봉을 나누는 체결 수와 간격(초)</summary>
    public const int PhasesPerMinute = 4;
    public const int PhaseSeconds = 15;

    private sealed class SymbolDay
    {
        public required StockInfo Info { get; init; }
        public required Dictionary<DateTimeOffset, Bar> Bars { get; init; }
        public required IReadOnlyList<Bar> DailyBefore { get; init; }
        public decimal PrevClose { get; init; }
        public decimal LastPrice;
        public decimal CumVolume;
        public decimal CumAmount;
        public OrderBookSnapshot? Book;
    }

    private readonly object _lock = new();
    private Dictionary<string, SymbolDay> _day = new();
    private HashSet<string> _tradeSubs = new();
    private HashSet<string> _bookSubs = new();
    private DateTimeOffset _now;

    public ReplayMarket(DateTimeOffset start) => _now = start;

    public DateTimeOffset Now => _now;
    public DateOnly Date { get; private set; }
    public IReadOnlyCollection<string> Symbols => _day.Keys;

    public event Action<TradeTick>? Trade;
    public event Action<OrderBookSnapshot>? OrderBook;
    public event Action<bool, string>? ConnectionChanged;
    public bool IsConnected => true;

    public void AdvanceTo(DateTimeOffset t)
    {
        if (t > _now) _now = t;
    }

    /// <summary>하루치 데이터 적재. minute: 종목별 당일 1분봉, dailyBefore: 종목별 전일까지 일봉.</summary>
    public void BeginDay(DateOnly date, IReadOnlyDictionary<string, IReadOnlyList<Bar>> minute,
        IReadOnlyDictionary<string, IReadOnlyList<Bar>> dailyBefore, IReadOnlyDictionary<string, StockInfo> infos)
    {
        var open = Kst.At(date, Kst.MarketOpen);
        var close = Kst.At(date, new TimeOnly(15, 30));
        var day = new Dictionary<string, SymbolDay>();
        foreach (var (sym, bars) in minute)
        {
            var daily = dailyBefore.TryGetValue(sym, out var d) ? d : Array.Empty<Bar>();
            var prev = daily.Count > 0 ? daily[^1].Close : bars.FirstOrDefault()?.Open ?? 0;
            var map = new Dictionary<DateTimeOffset, Bar>();
            foreach (var b in bars)
            {
                var start = Kst.MinuteStart(b.Start);
                if (start < open || start > close || b.Volume <= 0) continue; // 정규장만 (NXT 시간외 제외)
                map[start] = b;
            }
            if (map.Count == 0) continue;
            day[sym] = new SymbolDay
            {
                Info = infos.TryGetValue(sym, out var i) ? i : new StockInfo(sym, sym, "", "STOCK", true, false, false),
                Bars = map, DailyBefore = daily, PrevClose = prev, LastPrice = prev,
            };
        }
        lock (_lock) _day = day;
        Date = date;
    }

    /// <summary>minute 분의 phase 번째 체결을 한 번에 발생시킨다 (구간 사이 가격 없음). 시계는 호출자가 먼저 옮겨 둔다.</summary>
    public void EmitPhase(DateTimeOffset minute, int phase) => EmitStep(minute, phase, 1, 1);

    /// <summary>
    /// phase 구간(직전 꼭짓점 → 이번 꼭짓점)을 subCount 개 체결로 나눈 것 중 sub 번째 (1부터).
    /// 실제 시장은 가격이 연속으로 움직이므로 중간 가격을 채워 넣어야
    /// 돌파 진입이 봉 고가가 아니라 돌파 지점에서, 손절이 봉 저가가 아니라 손절가 근처에서 체결된다.
    /// </summary>
    public void EmitStep(DateTimeOffset minute, int phase, int sub, int subCount)
    {
        subCount = phase == 0 ? 1 : Math.Max(1, subCount);
        sub = Math.Clamp(sub, 1, subCount);
        foreach (var (sym, st) in _day)
        {
            if (!st.Bars.TryGetValue(minute, out var bar)) continue;
            var target = PathPrice(bar, phase);
            var from = phase == 0 ? target : PathPrice(bar, phase - 1);
            var price = sub == subCount ? target : InterpolateToTick(from, target, (decimal)sub / subCount);

            // 구간 거래량을 누적 비율로 나눠 합계가 정확히 맞게
            var phaseVol = phase < PhasesPerMinute - 1
                ? Math.Floor(bar.Volume / PhasesPerMinute)
                : bar.Volume - Math.Floor(bar.Volume / PhasesPerMinute) * (PhasesPerMinute - 1);
            var vol = Math.Floor(phaseVol * sub / subCount) - Math.Floor(phaseVol * (sub - 1) / subCount);
            if (vol <= 0) continue;

            var prev = st.LastPrice;
            var up = phase == 0 ? bar.Close >= bar.Open : price >= (prev > 0 ? prev : from);
            var tick = TickRules.TickSize(price);
            var (bid, ask) = up ? (price - tick, price) : (price, price + tick);
            var depth = Math.Max(1m, vol);
            var book = new OrderBookSnapshot(sym, _now,
                new[] { new PriceLevel(ask, depth), new PriceLevel(ask + tick, depth), new PriceLevel(ask + tick * 2, depth) },
                new[] { new PriceLevel(bid, depth), new PriceLevel(bid - tick, depth), new PriceLevel(bid - tick * 2, depth) });

            lock (_lock)
            {
                st.LastPrice = price;
                st.CumVolume += vol;
                st.CumAmount += price * vol;
                st.Book = book;
            }
            if (_bookSubs.Contains(sym) || _tradeSubs.Contains(sym)) OrderBook?.Invoke(book);
            if (_tradeSubs.Contains(sym)) Trade?.Invoke(new TradeTick(sym, price, vol, _now));
        }
    }

    /// <summary>from→to 사이 비율 위치를 호가 단위로 (출발점 쪽으로) 맞춘다</summary>
    public static decimal InterpolateToTick(decimal from, decimal to, decimal fraction)
    {
        var raw = from + (to - from) * fraction;
        return to >= from ? TickRules.RoundDown(raw) : TickRules.RoundUp(raw);
    }

    public static decimal PathPrice(Bar b, int phase)
    {
        var bullish = b.Close >= b.Open;
        return phase switch
        {
            0 => b.Open,
            1 => bullish ? b.Low : b.High,
            2 => bullish ? b.High : b.Low,
            _ => b.Close,
        };
    }

    /// <summary>오늘 재생 중인 종목의 1분봉 전체 (진단 기록용)</summary>
    public IReadOnlyList<Bar> DayBarsOf(string symbol) =>
        _day.TryGetValue(symbol, out var st) ? st.Bars.Values.OrderBy(b => b.Start).ToList() : Array.Empty<Bar>();

    /// <summary>현재 보유 평가용 종가 (당일 마지막 체결가)</summary>
    public decimal? LastPriceOf(string symbol) => _day.TryGetValue(symbol, out var st) && st.LastPrice > 0 ? st.LastPrice : null;

    // ================================================================ IMarketDataFeed

    public Task StartAsync(CancellationToken ct)
    {
        ConnectionChanged?.Invoke(true, "과거 데이터 재생");
        return Task.CompletedTask;
    }

    public Task SetSubscriptionsAsync(IReadOnlyCollection<string> tradeSymbols, IReadOnlyCollection<string> orderBookSymbols, CancellationToken ct)
    {
        _tradeSubs = tradeSymbols.ToHashSet();
        _bookSubs = orderBookSymbols.ToHashSet();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ================================================================ IMarketDataSource (지금 시각까지만)

    public Task<IReadOnlyList<RankingEntry>> GetRankingsAsync(RankingType type, int count, CancellationToken ct)
    {
        List<RankingEntry> list;
        lock (_lock)
        {
            list = _day.Where(kv => kv.Value.CumVolume > 0)
                .Select(kv => (kv.Key, S: kv.Value))
                .OrderByDescending(x => type switch
                {
                    RankingType.TradingVolume => x.S.CumVolume,
                    RankingType.TopGainers => x.S.PrevClose > 0 ? x.S.LastPrice / x.S.PrevClose : 0,
                    _ => x.S.CumAmount,
                })
                .Take(count)
                .Select((x, i) => new RankingEntry(i + 1, x.Key, x.S.LastPrice, x.S.PrevClose,
                    x.S.PrevClose > 0 ? x.S.LastPrice / x.S.PrevClose - 1m : null, x.S.CumVolume, x.S.CumAmount))
                .ToList();
        }
        return Task.FromResult<IReadOnlyList<RankingEntry>>(list);
    }

    public Task<IReadOnlyList<PriceQuote>> GetPricesAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        IReadOnlyList<PriceQuote> r;
        lock (_lock)
            r = symbols.Where(_day.ContainsKey).Select(s => new PriceQuote(s, _day[s].LastPrice, _now)).ToList();
        return Task.FromResult(r);
    }

    public Task<IReadOnlyList<Bar>> GetTodayMinuteBarsAsync(string symbol, CancellationToken ct) => Task.FromResult(CompletedBars(symbol));

    public Task<IReadOnlyList<Bar>> GetLatestSessionMinuteBarsAsync(string symbol, CancellationToken ct) => Task.FromResult(CompletedBars(symbol));

    private IReadOnlyList<Bar> CompletedBars(string symbol)
    {
        if (!_day.TryGetValue(symbol, out var st)) return Array.Empty<Bar>();
        var now = _now;
        return st.Bars.Values.Where(b => b.Start.AddMinutes(1) <= now).OrderBy(b => b.Start).Select(b => b.Clone()).ToList();
    }

    public Task<IReadOnlyList<Bar>> GetDailyBarsAsync(string symbol, int count, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Bar>>(_day.TryGetValue(symbol, out var st) ? st.DailyBefore.TakeLast(count).ToList() : Array.Empty<Bar>());

    public Task<IReadOnlyList<StockInfo>> GetStocksAsync(IReadOnlyList<string> symbols, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StockInfo>>(symbols.Where(_day.ContainsKey).Select(s => _day[s].Info).ToList());

    public Task<IReadOnlyList<StockWarning>> GetWarningsAsync(string symbol, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StockWarning>>(Array.Empty<StockWarning>());

    public Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, CancellationToken ct)
    {
        lock (_lock) return Task.FromResult(_day.TryGetValue(symbol, out var st) ? st.Book : null);
    }

    public Task<PriceLimits?> GetPriceLimitsAsync(string symbol, CancellationToken ct)
    {
        if (!_day.TryGetValue(symbol, out var st) || st.PrevClose <= 0) return Task.FromResult<PriceLimits?>(null);
        var upper = TickRules.RoundDown(st.PrevClose * 1.3m);
        var lower = TickRules.RoundUp(st.PrevClose * 0.7m);
        return Task.FromResult<PriceLimits?>(new PriceLimits(upper, lower));
    }
}
