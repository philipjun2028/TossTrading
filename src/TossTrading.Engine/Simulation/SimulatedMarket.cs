using System.Diagnostics;
using TossTrading.Domain;

namespace TossTrading.Engine.Simulation;

public sealed class SimulationOptions
{
    /// <summary>가상 시간 배속 (5 = 실제 1분에 5분 진행)</summary>
    public double Speed { get; set; } = 5;
    public int SymbolCount { get; set; } = 40;
    public int InPlayCount { get; set; } = 8;
    public int? Seed { get; set; }
    public TimeOnly StartTime { get; set; } = new(9, 0);

    /// <summary>true 면 실시간 진행 없이 AdvanceTo 로만 시간이 흐른다 (테스트용)</summary>
    public bool ManualClock { get; set; }
}

/// <summary>
/// API 키 없이 앱을 체험/테스트하기 위한 가상 시장.
/// 랜덤워크 + 급등 구간(burst) + 갭상승 "Stocks in Play" 종목을 생성하며,
/// 시세 스트림(IMarketDataFeed), 조회(IMarketDataSource), 가상 시계(IClock)를 모두 제공한다.
/// 생성되는 종목·가격은 전부 가상이며 실제 종목과 무관하다.
/// </summary>
public sealed class SimulatedMarket : IMarketDataFeed, IMarketDataSource, IClock
{
    private sealed class SimSymbol
    {
        public required string Code;
        public required string Name;
        public decimal PrevClose;
        public decimal Anchor;
        public decimal Price;
        public bool InPlay;
        public double TradeRate;         // 가상 초당 체결 수
        public decimal TradeSize;        // 평균 체결 수량
        public decimal AvgDailyVolume;
        public double BurstBias;
        public double BurstRemaining;    // 가상 초
        public decimal Open, High, Low, Volume, Value;
        public readonly List<Bar> Bars = new();
        public Bar? Current;
        public OrderBookSnapshot? Book;
        public DateTimeOffset LastBookSent;
        public readonly List<Bar> Daily = new();
        public bool Overheated;
        public bool IsEtf;
    }

    private static readonly string[] Prefixes = { "한결", "누리", "가온", "다온", "라온", "미르", "새봄", "아라", "온새", "하람" };
    private static readonly string[] Suffixes = { "전자", "바이오", "소재", "로보틱스" };

    private readonly SimulationOptions _o;
    private readonly Random _rng;
    private readonly object _lock = new();
    private readonly List<SimSymbol> _symbols = new();
    private readonly Dictionary<string, SimSymbol> _byCode = new();
    private readonly Stopwatch _sw = new();
    private readonly DateTimeOffset _virtualStart;
    private readonly DateTimeOffset _sessionEnd;
    private HashSet<string> _tradeSubs = new();
    private HashSet<string> _bookSubs = new();
    private DateTimeOffset _simulatedUntil;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SimulatedMarket(SimulationOptions? options = null)
    {
        _o = options ?? new SimulationOptions();
        _rng = _o.Seed is { } s ? new Random(s) : new Random();
        var today = Kst.DateOf(Kst.Now);
        _virtualStart = Kst.At(today, _o.StartTime);
        _sessionEnd = Kst.At(today, Kst.MarketClose);
        _simulatedUntil = _virtualStart;
        CreateSymbols(today);
    }

    public event Action<TradeTick>? Trade;
    public event Action<OrderBookSnapshot>? OrderBook;
    public event Action<bool, string>? ConnectionChanged;

    public bool IsConnected { get; private set; }

    public double Speed => _o.Speed;

    public DateTimeOffset Now
    {
        get
        {
            if (!_sw.IsRunning) lock (_lock) return _simulatedUntil; // 테스트: AdvanceTo 로 수동 진행
            var t = _virtualStart + TimeSpan.FromSeconds(_sw.Elapsed.TotalSeconds * _o.Speed);
            return t > _sessionEnd ? _sessionEnd : t;
        }
    }

    public IReadOnlyList<string> Symbols
    {
        get { lock (_lock) return _symbols.Select(s => s.Code).ToList(); }
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (_loop is not null || IsConnected) return Task.CompletedTask;
        IsConnected = true;
        ConnectionChanged?.Invoke(true, "시뮬레이션 시장 시작");
        if (_o.ManualClock) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _sw.Start();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>테스트용: 실시간 대기 없이 가상 시간을 직접 진행시킨다.</summary>
    public void AdvanceTo(DateTimeOffset until) => Generate(until);

    public Task SetSubscriptionsAsync(IReadOnlyCollection<string> tradeSymbols, IReadOnlyCollection<string> orderBookSymbols, CancellationToken ct)
    {
        lock (_lock)
        {
            _tradeSubs = tradeSymbols.ToHashSet();
            _bookSubs = orderBookSymbols.ToHashSet();
        }
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(100, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            Generate(Now);
        }
    }

    // ================================================================== 생성

    private void CreateSymbols(DateOnly today)
    {
        var inPlay = new HashSet<int>();
        while (inPlay.Count < Math.Min(_o.InPlayCount, _o.SymbolCount)) inPlay.Add(_rng.Next(_o.SymbolCount));

        for (var i = 0; i < _o.SymbolCount; i++)
        {
            var prev = TickRules.RoundDown((decimal)Math.Exp(Math.Log(1500) + _rng.NextDouble() * (Math.Log(90000) - Math.Log(1500))));
            var s = new SimSymbol
            {
                Code = $"9{i + 10:D5}",
                Name = Prefixes[i % Prefixes.Length] + Suffixes[i / Prefixes.Length % Suffixes.Length],
                PrevClose = prev,
                InPlay = inPlay.Contains(i),
                AvgDailyVolume = (decimal)Math.Round(8_000_000_000d / (double)prev * (0.3 + _rng.NextDouble())),
                IsEtf = i == 3,
                Overheated = i == 7 && !inPlay.Contains(i),
            };
            s.TradeRate = s.InPlay ? 2.5 : 0.8;
            var volMultiplier = s.InPlay ? 3 + _rng.NextDouble() * 4 : 0.7 + _rng.NextDouble() * 0.6;
            s.TradeSize = Math.Max(1, Math.Round(s.AvgDailyVolume * (decimal)volMultiplier / (decimal)(390 * 60 * s.TradeRate)));
            var gap = s.InPlay ? 0.03 + _rng.NextDouble() * 0.09 : (_rng.NextDouble() - 0.5) * 0.02;
            s.Price = TickRules.RoundDown(prev * (decimal)(1 + gap));
            s.Anchor = s.Price;

            // 과거 20일 일봉 (RVOL 계산용)
            var p = prev;
            for (var d = 20; d >= 1; d--)
            {
                var o = p;
                p = TickRules.RoundDown(p * (decimal)(1 + (_rng.NextDouble() - 0.5) * 0.04));
                var date = today.AddDays(-d);
                s.Daily.Add(new Bar
                {
                    Start = Kst.At(date, TimeOnly.MinValue), Open = o, Close = p,
                    High = Math.Max(o, p) * 1.01m, Low = Math.Min(o, p) * 0.99m,
                    Volume = Math.Round(s.AvgDailyVolume * (decimal)(0.7 + _rng.NextDouble() * 0.6)),
                });
            }
            s.Daily[^1].Close = prev;
            s.Book = MakeBook(s, _virtualStart, buyerInitiated: true);
            _symbols.Add(s);
            _byCode[s.Code] = s;
        }
    }

    private void Generate(DateTimeOffset until)
    {
        var trades = new List<TradeTick>();
        var books = new List<OrderBookSnapshot>();
        lock (_lock)
        {
            if (until > _sessionEnd) until = _sessionEnd;
            const double step = 0.5; // 가상 0.5초 단위로 진행
            while (_simulatedUntil + TimeSpan.FromSeconds(step) <= until)
            {
                _simulatedUntil += TimeSpan.FromSeconds(step);
                foreach (var s in _symbols) Step(s, _simulatedUntil, step, trades, books);
            }
        }
        foreach (var t in trades) Trade?.Invoke(t);
        foreach (var b in books) OrderBook?.Invoke(b);
    }

    private void Step(SimSymbol s, DateTimeOffset now, double dt, List<TradeTick> trades, List<OrderBookSnapshot> books)
    {
        // 급등/급락 구간 시작
        if (s.BurstRemaining <= 0)
        {
            var p = (s.InPlay ? 0.004 : 0.0006) * dt;
            if (_rng.NextDouble() < p)
            {
                s.BurstRemaining = 60 + _rng.NextDouble() * 240;
                var up = _rng.NextDouble() < (s.InPlay ? 0.7 : 0.5);
                s.BurstBias = up ? 0.10 + _rng.NextDouble() * 0.08 : -(0.08 + _rng.NextDouble() * 0.06);
            }
        }
        else
        {
            s.BurstRemaining -= dt;
            if (s.BurstRemaining <= 0) { s.BurstBias = 0; s.Anchor = s.Price; }
        }

        var bursting = s.BurstRemaining > 0;
        // 장 초반에 거래가 몰리는 형태 (실제 장과 비슷하게)
        var minutes = (now - _virtualStart).TotalMinutes;
        var intraday = 1 + 2.5 * Math.Exp(-minutes / 30);
        var lambda = s.TradeRate * dt * intraday * (bursting ? 3 : 1);
        var n = (int)Math.Floor(lambda) + (_rng.NextDouble() < lambda - Math.Floor(lambda) ? 1 : 0);
        var upper = TickRules.RoundDown(s.PrevClose * 1.3m);
        var lower = TickRules.RoundUp(s.PrevClose * 0.7m);

        for (var i = 0; i < n; i++)
        {
            var deviation = (double)(s.Price / s.Anchor - 1m);
            var bias = s.BurstBias + (s.InPlay ? 0.02 : 0) - deviation * 3;
            bias = Math.Clamp(bias, -0.4, 0.4);
            var buyer = _rng.NextDouble() < 0.5 + bias;
            if (_rng.NextDouble() < 0.25)
                s.Price = TickRules.Clamp(TickRules.AddTicks(s.Price, buyer ? 1 : -1), lower, upper);

            var vol = Math.Max(1, Math.Round(s.TradeSize * (decimal)Math.Exp(_rng.NextDouble() * 1.6 - 0.8)));
            var ts = now - TimeSpan.FromMilliseconds(_rng.NextDouble() * dt * 1000);
            ApplyTrade(s, ts, s.Price, vol);
            s.Book = MakeBook(s, ts, buyer);
            if (_tradeSubs.Contains(s.Code)) trades.Add(new TradeTick(s.Code, s.Price, vol, ts));
        }

        if (_bookSubs.Contains(s.Code) && s.Book is not null && now - s.LastBookSent >= TimeSpan.FromSeconds(1))
        {
            s.LastBookSent = now;
            books.Add(s.Book);
        }
    }

    private void ApplyTrade(SimSymbol s, DateTimeOffset ts, decimal price, decimal vol)
    {
        if (s.Open == 0) { s.Open = price; s.High = price; s.Low = price; }
        s.High = Math.Max(s.High, price);
        s.Low = Math.Min(s.Low, price);
        s.Volume += vol;
        s.Value += price * vol;
        var minute = Kst.MinuteStart(ts);
        if (s.Current is null || minute > s.Current.Start)
        {
            if (s.Current is not null) s.Bars.Add(s.Current);
            s.Current = Bar.FromTrade(minute, price, vol);
        }
        else
        {
            s.Current.Apply(price, vol);
        }
    }

    private OrderBookSnapshot MakeBook(SimSymbol s, DateTimeOffset ts, bool buyerInitiated)
    {
        // 매수 주도 체결이면 체결가 = 매도1호가, 아니면 체결가 = 매수1호가
        var ask1 = buyerInitiated ? s.Price : TickRules.AddTicks(s.Price, 1);
        var bid1 = TickRules.AddTicks(ask1, -1);
        var asks = new List<PriceLevel>();
        var bids = new List<PriceLevel>();
        decimal a = ask1, b = bid1;
        for (var i = 0; i < 10; i++)
        {
            asks.Add(new PriceLevel(a, Math.Round(s.TradeSize * (decimal)(2 + _rng.NextDouble() * 8))));
            bids.Add(new PriceLevel(b, Math.Round(s.TradeSize * (decimal)(2 + _rng.NextDouble() * 8))));
            a = TickRules.AddTicks(a, 1);
            b = TickRules.AddTicks(b, -1);
        }
        return new OrderBookSnapshot(s.Code, ts, asks, bids);
    }

    // ================================================================== IMarketDataSource

    public Task<IReadOnlyList<RankingEntry>> GetRankingsAsync(RankingType type, int count, CancellationToken ct)
    {
        lock (_lock)
        {
            IEnumerable<SimSymbol> q = type switch
            {
                RankingType.TradingAmount => _symbols.OrderByDescending(s => s.Value),
                RankingType.TradingVolume => _symbols.OrderByDescending(s => s.Volume),
                _ => _symbols.OrderByDescending(s => s.Price / s.PrevClose),
            };
            IReadOnlyList<RankingEntry> list = q.Take(count).Select((s, i) => new RankingEntry(
                i + 1, s.Code, s.Price, s.PrevClose, s.Price / s.PrevClose - 1m, s.Volume, s.Value)).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<IReadOnlyList<PriceQuote>> GetPricesAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        lock (_lock)
        {
            IReadOnlyList<PriceQuote> list = symbols.Where(_byCode.ContainsKey)
                .Select(c => new PriceQuote(c, _byCode[c].Price, _simulatedUntil)).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<IReadOnlyList<Bar>> GetTodayMinuteBarsAsync(string symbol, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_byCode.TryGetValue(symbol, out var s)) return Task.FromResult<IReadOnlyList<Bar>>(Array.Empty<Bar>());
            var bars = s.Bars.Select(b => b.Clone()).ToList();
            return Task.FromResult<IReadOnlyList<Bar>>(bars);
        }
    }

    public Task<IReadOnlyList<Bar>> GetDailyBarsAsync(string symbol, int count, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_byCode.TryGetValue(symbol, out var s)) return Task.FromResult<IReadOnlyList<Bar>>(Array.Empty<Bar>());
            return Task.FromResult<IReadOnlyList<Bar>>(s.Daily.TakeLast(count).Select(b => b.Clone()).ToList());
        }
    }

    public Task<IReadOnlyList<StockInfo>> GetStocksAsync(IReadOnlyList<string> symbols, CancellationToken ct)
    {
        lock (_lock)
        {
            IReadOnlyList<StockInfo> list = symbols.Where(_byCode.ContainsKey).Select(c =>
            {
                var s = _byCode[c];
                return new StockInfo(c, s.Name, "KOSDAQ", s.IsEtf ? "ETF" : "STOCK", !s.IsEtf, false, false);
            }).ToList();
            return Task.FromResult(list);
        }
    }

    public Task<IReadOnlyList<StockWarning>> GetWarningsAsync(string symbol, CancellationToken ct)
    {
        lock (_lock)
        {
            IReadOnlyList<StockWarning> list = _byCode.TryGetValue(symbol, out var s) && s.Overheated
                ? new[] { new StockWarning("OVERHEATED", Kst.DateOf(_virtualStart), null) }
                : Array.Empty<StockWarning>();
            return Task.FromResult(list);
        }
    }

    public Task<OrderBookSnapshot?> GetOrderBookAsync(string symbol, CancellationToken ct)
    {
        lock (_lock) return Task.FromResult(_byCode.TryGetValue(symbol, out var s) ? s.Book : null);
    }

    public Task<PriceLimits?> GetPriceLimitsAsync(string symbol, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_byCode.TryGetValue(symbol, out var s)) return Task.FromResult<PriceLimits?>(null);
            return Task.FromResult<PriceLimits?>(new PriceLimits(TickRules.RoundDown(s.PrevClose * 1.3m), TickRules.RoundUp(s.PrevClose * 0.7m)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* 종료 */ }
        }
        IsConnected = false;
    }
}
