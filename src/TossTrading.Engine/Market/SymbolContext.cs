using TossTrading.Domain;

namespace TossTrading.Engine.Market;

/// <summary>
/// 종목 1개의 장중 상태: 1분봉, VWAP, 당일 고저, 시가범위(ORB), 체결강도, 최신 호가, ATR.
/// 엔진 이벤트 루프 스레드에서만 수정된다 (락 없음).
/// </summary>
public sealed class SymbolContext
{
    private const int MaxBars = 420;
    private readonly List<Bar> _bars = new();   // 마감된 1분봉
    private decimal _lastPrice;

    public SymbolContext(string symbol, string name, MarketCountry market = MarketCountry.KR)
    {
        Symbol = symbol;
        Name = name;
        Market = market;
    }

    public string Symbol { get; }
    public string Name { get; set; }
    public MarketCountry Market { get; }

    public decimal LastPrice => _lastPrice;
    public DateTimeOffset LastTradeTime { get; private set; }
    public decimal? PreviousClose { get; set; }
    public PriceLimits? Limits { get; set; }

    public decimal DayOpen { get; private set; }
    public decimal DayHigh { get; private set; }
    public decimal DayLow { get; private set; }
    public decimal CumVolume { get; private set; }
    public decimal CumValue { get; private set; }
    public decimal Vwap => CumVolume > 0 ? CumValue / CumVolume : _lastPrice;

    /// <summary>매수 주도 / 매도 주도 체결량 (체결강도 계산용)</summary>
    public decimal BuyVolume { get; private set; }
    public decimal SellVolume { get; private set; }

    /// <summary>체결강도 = 매수주도 ÷ 매도주도 × 100. 데이터 없으면 null.</summary>
    public decimal? Strength => SellVolume > 0 ? BuyVolume / SellVolume * 100m : BuyVolume > 0 ? 300m : null;

    public OrderBookSnapshot? OrderBook { get; private set; }
    public Bar? CurrentBar { get; private set; }
    public IReadOnlyList<Bar> Bars => _bars;
    public bool HasData => _lastPrice > 0;

    public decimal? ChangeRate => PreviousClose is > 0 && _lastPrice > 0 ? _lastPrice / PreviousClose.Value - 1m : null;

    /// <summary>체결 반영. 새 분봉이 시작되며 이전 봉이 마감되면 true.</summary>
    public bool OnTrade(TradeTick t)
    {
        if (t.Price <= 0 || t.Volume <= 0) return false;

        // 체결 주도 방향: 호가 기준, 없으면 틱 규칙
        var book = OrderBook;
        if (book?.BestAsk is { } ask && t.Price >= ask) BuyVolume += t.Volume;
        else if (book?.BestBid is { } bid && t.Price <= bid) SellVolume += t.Volume;
        else if (_lastPrice > 0 && t.Price > _lastPrice) BuyVolume += t.Volume;
        else if (_lastPrice > 0 && t.Price < _lastPrice) SellVolume += t.Volume;

        if (DayOpen == 0) { DayOpen = t.Price; DayHigh = t.Price; DayLow = t.Price; }
        if (t.Price > DayHigh) DayHigh = t.Price;
        if (t.Price < DayLow) DayLow = t.Price;
        CumVolume += t.Volume;
        CumValue += t.Price * t.Volume;
        _lastPrice = t.Price;
        LastTradeTime = t.Timestamp;

        var minute = Kst.MinuteStart(t.Timestamp);
        var closed = false;
        if (CurrentBar is null)
        {
            CurrentBar = Bar.FromTrade(minute, t.Price, t.Volume);
        }
        else if (minute > CurrentBar.Start)
        {
            CloseCurrentBar();
            CurrentBar = Bar.FromTrade(minute, t.Price, t.Volume);
            closed = true;
        }
        else
        {
            CurrentBar.Apply(t.Price, t.Volume);
        }
        return closed;
    }

    public void OnOrderBook(OrderBookSnapshot book) => OrderBook = book;

    /// <summary>체결이 없어도 분이 넘어가면 봉을 마감한다. 마감되면 true.</summary>
    public bool OnTimer(DateTimeOffset now)
    {
        if (CurrentBar is null) return false;
        if (Kst.MinuteStart(now) > CurrentBar.Start)
        {
            CloseCurrentBar();
            CurrentBar = null;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 늦게 시작한 경우 REST 1분봉으로 과거 상태를 복원한다.
    /// 라이브 체결이 시작된 분 이전의 봉만 반영한다 (중복 집계 방지).
    /// </summary>
    public void Seed(IReadOnlyList<Bar> minuteBars)
    {
        var liveStart = _bars.Count > 0 ? _bars[0].Start : CurrentBar?.Start ?? DateTimeOffset.MaxValue;
        var older = minuteBars.Where(b => b.Start < liveStart).OrderBy(b => b.Start).ToList();
        if (older.Count == 0) return;

        DayOpen = older[0].Open; // 시드 봉이 라이브보다 앞서므로 당일 시가는 시드의 첫 봉
        foreach (var b in older)
        {
            var value = b.Value > 0 ? b.Value : b.TypicalPrice * b.Volume;
            CumVolume += b.Volume;
            CumValue += value;
            DayHigh = DayHigh == 0 ? b.High : Math.Max(DayHigh, b.High);
            DayLow = DayLow == 0 ? b.Low : Math.Min(DayLow, b.Low);
        }
        _bars.InsertRange(0, older.Select(b => { var c = b.Clone(); if (c.Value <= 0) c.Value = c.TypicalPrice * c.Volume; return c; }));
        if (_bars.Count > MaxBars) _bars.RemoveRange(0, _bars.Count - MaxBars);
        if (_lastPrice == 0) _lastPrice = older[^1].Close;
    }

    // ------------------------------------------------------------------ 파생 지표

    /// <summary>시가범위 (09:00 부터 N분). 범위가 아직 안 끝났으면 Complete=false.</summary>
    public (decimal High, decimal Low, bool Complete) OpeningRange(int minutes, DateTimeOffset now)
    {
        var open = Kst.At(Kst.DateOf(now), Kst.MarketOpen);
        var end = open.AddMinutes(minutes);
        decimal hi = 0, lo = 0;
        foreach (var b in AllBars())
        {
            if (b.Start < open || b.Start >= end) continue;
            hi = hi == 0 ? b.High : Math.Max(hi, b.High);
            lo = lo == 0 ? b.Low : Math.Min(lo, b.Low);
        }
        return (hi, lo, now >= end && hi > 0);
    }

    /// <summary>1분봉 ATR (단순 평균 True Range)</summary>
    public decimal? Atr(int period = 14)
    {
        if (_bars.Count < 2) return null;
        var n = Math.Min(period, _bars.Count - 1);
        decimal sum = 0;
        for (var i = _bars.Count - n; i < _bars.Count; i++)
        {
            var b = _bars[i];
            var prevClose = _bars[i - 1].Close;
            sum += Math.Max(b.High - b.Low, Math.Max(Math.Abs(b.High - prevClose), Math.Abs(b.Low - prevClose)));
        }
        return sum / n;
    }

    /// <summary>최근 N개 마감봉 평균 거래량</summary>
    public decimal AverageBarVolume(int n)
    {
        if (_bars.Count == 0) return 0;
        var take = Math.Min(n, _bars.Count);
        decimal sum = 0;
        for (var i = _bars.Count - take; i < _bars.Count; i++) sum += _bars[i].Volume;
        return sum / take;
    }

    public Bar? LastClosedBar => _bars.Count > 0 ? _bars[^1] : null;

    public Bar? ClosedBarFromEnd(int indexFromEnd) =>
        _bars.Count > indexFromEnd ? _bars[_bars.Count - 1 - indexFromEnd] : null;

    /// <summary>당일 범위 내 현재가 위치 0~1</summary>
    public decimal? RangePosition => DayHigh > DayLow ? (_lastPrice - DayLow) / (DayHigh - DayLow) : null;

    public IEnumerable<Bar> AllBars()
    {
        foreach (var b in _bars) yield return b;
        if (CurrentBar is not null) yield return CurrentBar;
    }

    private void CloseCurrentBar()
    {
        if (CurrentBar is null) return;
        _bars.Add(CurrentBar);
        if (_bars.Count > MaxBars) _bars.RemoveAt(0);
    }
}
