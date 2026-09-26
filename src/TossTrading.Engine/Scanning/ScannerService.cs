using System.Collections.Concurrent;
using TossTrading.Domain;

namespace TossTrading.Engine.Scanning;

/// <summary>엔진이 실시간 구독 중인 종목의 지표 (스캐너 점수 보강용)</summary>
public sealed record LiveMetrics(decimal LastPrice, decimal Vwap, decimal? Strength, int? SpreadTicks, decimal? RangePosition);

/// <summary>
/// Stocks in Play 스캐너 (설계 문서 5장).
/// 랭킹(거래대금·거래량·상승률) → 합집합 → 필터(경고/유형/가격/등락률/거래대금/RVOL/틱비용) → 점수화.
/// 자체 백그라운드 루프에서 돌며, 결과를 콜백으로 엔진에 넘긴다.
/// </summary>
public sealed class ScannerService : IAsyncDisposable
{
    private const int FetchPerCycle = 6;

    private readonly IMarketDataSource _source;
    private readonly IClock _clock;
    private readonly Func<string, LiveMetrics?> _live;
    private readonly Action<IReadOnlyList<ScanCandidate>> _onResult;
    private readonly Action<LogLevel, string> _log;
    private readonly ConcurrentDictionary<string, StockInfo> _stocks = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<StockWarning>> _warnings = new();
    private readonly ConcurrentDictionary<string, decimal> _avgDailyVolume = new();
    private readonly ConcurrentDictionary<string, IntradayStats> _intraday = new();
    private int _intradayErrors;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile ScannerSettings _settings;

    public ScannerService(
        IMarketDataSource source, IClock clock, ScannerSettings settings,
        Func<string, LiveMetrics?> live, Action<IReadOnlyList<ScanCandidate>> onResult, Action<LogLevel, string> log)
    {
        _source = source;
        _clock = clock;
        _settings = settings.Clone();
        _live = live;
        _onResult = onResult;
        _log = log;
    }

    public void UpdateSettings(ScannerSettings s) => _settings = s.Clone();

    public string? NameOf(string symbol) => _stocks.TryGetValue(symbol, out var s) ? s.Name : null;

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await ScanOnceAsync(ct).ConfigureAwait(false);
                _onResult(result);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (failures++ < 3 || failures % 20 == 0) _log(LogLevel.Warn, $"스캐너 오류: {ex.Message}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, _settings.PollSeconds)), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task<IReadOnlyList<ScanCandidate>> ScanOnceAsync(CancellationToken ct)
    {
        var s = _settings;
        var now = _clock.Now;

        // 1) 랭킹 합집합 (전일 종가 기준 entry 우선)
        var merged = new Dictionary<string, RankingEntry>();
        foreach (var type in new[] { RankingType.TradingAmount, RankingType.TradingVolume, RankingType.TopGainers })
        {
            var list = await _source.GetRankingsAsync(type, 100, ct).ConfigureAwait(false);
            foreach (var e in list)
                if (!merged.ContainsKey(e.Symbol)) merged[e.Symbol] = e;
        }
        if (merged.Count == 0) return Array.Empty<ScanCandidate>();

        // 2) 종목정보 (신규만 일괄 조회)
        var unknown = merged.Keys.Where(k => !_stocks.ContainsKey(k)).Take(200).ToList();
        if (unknown.Count > 0)
            foreach (var info in await _source.GetStocksAsync(unknown, ct).ConfigureAwait(false))
                _stocks[info.Symbol] = info;

        // 3) 1차 필터 (가격/등락률/틱비용) → 통과 종목만 경고/일봉 보강
        var fraction = ExpectedVolumeFraction(now);
        var amountThreshold = s.MinTradingAmount * Math.Min(1m, fraction / ExpectedVolumeFraction(Kst.At(Kst.DateOf(now), new TimeOnly(10, 0))));
        var closingMode = s.Mode == ScanMode.ClosingBet;
        var (minChange, maxChange) = closingMode ? (s.ClosingMinChangePct, s.ClosingMaxChangePct) : (s.MinChangePct, s.MaxChangePct);
        var pre = new List<(RankingEntry E, decimal ChangePct, decimal TickCostPct)>();
        foreach (var e in merged.Values)
        {
            var change = (e.BasePrice > 0 ? e.LastPrice / e.BasePrice - 1m : e.ChangeRate ?? 0) * 100m;
            var tickCost = TickRules.TickCostRate(e.LastPrice) * 100m;
            if (e.LastPrice < s.MinPrice) continue;
            if (change < minChange || change > maxChange) continue;
            if (tickCost > s.MaxTickCostPct) continue;
            if (e.TradingAmount < amountThreshold) continue;
            if (_stocks.TryGetValue(e.Symbol, out var info))
            {
                if (info.TradingSuspended || info.LiquidationTrading) continue;
                if (s.ExcludeNonCommonStock && (info.SecurityType != "STOCK" || !info.IsCommonShare)) continue;
            }
            pre.Add((e, change, tickCost));
        }

        var needWarn = pre.Select(p => p.E.Symbol).Where(x => !_warnings.ContainsKey(x)).Take(FetchPerCycle).ToList();
        foreach (var sym in needWarn)
            _warnings[sym] = await _source.GetWarningsAsync(sym, ct).ConfigureAwait(false);
        var needDaily = pre.Select(p => p.E.Symbol).Where(x => !_avgDailyVolume.ContainsKey(x)).Take(FetchPerCycle).ToList();
        foreach (var sym in needDaily)
        {
            var bars = await _source.GetDailyBarsAsync(sym, 20, ct).ConfigureAwait(false);
            _avgDailyVolume[sym] = bars.Count > 0 ? bars.Average(b => b.Volume) : 0;
        }

        // 3-2) 종가매매 모드: 후보 전체의 당일 분봉으로 고저 위치·VWAP·30분 추세 계산
        //      (실시간 구독 중인 상위 종목만이 아니라 전부. 호출 한도를 위해 한 번에 일부씩, 오래된 것부터 갱신)
        if (closingMode)
        {
            var refresh = TimeSpan.FromSeconds(Math.Max(10, s.ClosingBarsRefreshSeconds));
            var targets = pre.OrderByDescending(p => p.E.TradingAmount).Select(p => p.E.Symbol)
                .Select(sym => (Sym: sym, Age: _intraday.TryGetValue(sym, out var st) ? now - st.FetchedAt : TimeSpan.MaxValue))
                .Where(x => x.Age >= refresh)
                .OrderByDescending(x => x.Age)
                .Take(Math.Max(1, s.ClosingBarsPerCycle))
                .Select(x => x.Sym).ToList();
            foreach (var sym in targets)
            {
                try
                {
                    var bars = await _source.GetLatestSessionMinuteBarsAsync(sym, ct).ConfigureAwait(false);
                    if (ComputeIntraday(bars, now) is { } stats) _intraday[sym] = stats;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 한 종목 실패로 스캔 전체가 멈추지 않도록. 다음 주기에 다시 시도
                    if (_intradayErrors++ % 20 == 0) _log(LogLevel.Warn, $"분봉 조회 실패 {sym}: {ex.Message}");
                }
            }
        }

        // 4) 최종 필터 + 점수
        var result = new List<ScanCandidate>();
        foreach (var (e, change, tickCost) in pre)
        {
            var tags = new List<string>();
            if (_warnings.TryGetValue(e.Symbol, out var warns))
            {
                if (warns.Any(w => w.IsBlocking)) continue;
                if (warns.Any(w => w.IsVi)) tags.Add("VI");
            }
            else tags.Add("경고?");

            decimal? rvol = null;
            if (_avgDailyVolume.TryGetValue(e.Symbol, out var avg) && avg > 0)
            {
                rvol = e.TradingVolume / (avg * fraction);
                if (!closingMode && rvol < s.MinRvol) continue; // 종가매매는 RVOL 대신 거래대금으로 유동성 판단
            }
            else if (!closingMode) tags.Add("RVOL?");

            var live = _live(e.Symbol);
            if (live?.SpreadTicks is { } spread && spread > s.MaxSpreadTicks) continue;
            decimal? vwapDist = live is { Vwap: > 0 } ? (live.LastPrice / live.Vwap - 1m) * 100m : null;

            if (change >= 15m) tags.Add("과열근접");
            if (vwapDist is { } vd && vd is >= 0 and <= 1m) tags.Add("VWAP근접");
            if (live?.RangePosition is >= 0.95m) tags.Add("신고가근접");
            // 종가매매 후보: 14:30~15:20, 강세(+3~20%), 고가 부근(범위 상단 75% 이상), VWAP 위
            var tNow = Kst.TimeOf(now);
            if (!closingMode && tNow >= new TimeOnly(14, 30) && tNow < new TimeOnly(15, 20) && change is >= 3m and <= 20m
                && live?.RangePosition is >= 0.75m && vwapDist is >= 0m)
                tags.Add("종가후보");

            var name = _stocks.TryGetValue(e.Symbol, out var st) ? st.Name : e.Symbol;

            if (closingMode)
            {
                _intraday.TryGetValue(e.Symbol, out var intra);
                var rangePos = live?.RangePosition ?? RangePositionOf(e.LastPrice, intra);
                var vwap = live is { Vwap: > 0 } ? live.Vwap : intra?.Vwap;
                decimal? dist = vwap is > 0 ? (e.LastPrice / vwap.Value - 1m) * 100m : null;
                decimal? trend = intra?.Close30mAgo is > 0 ? (e.LastPrice / intra.Close30mAgo.Value - 1m) * 100m : null;
                var eval = EvaluateClosing(change, rangePos, dist, trend, live?.Strength, e.TradingAmount, s);
                if (intra is null && live is null) tags.Add("분봉대기");
                else if (live is null && intra is not null && intra.SessionDate < Kst.DateOf(now)) tags.Add($"직전장({intra.SessionDate:MM/dd})");
                if (eval.AllPassed) tags.Add("종가후보");
                result.Add(new ScanCandidate(e.Symbol, name, e.LastPrice, change, e.TradingAmount, rvol, live?.Strength,
                    tickCost, live?.SpreadTicks, dist, rangePos, eval.Score, string.Join(" ", tags),
                    trend, eval.Checks, eval.Passed, eval.Total));
                continue;
            }

            var score = Score(rvol, e.TradingAmount, live?.Strength, vwapDist, live?.RangePosition, tickCost, s);
            result.Add(new ScanCandidate(e.Symbol, name, e.LastPrice, change, e.TradingAmount, rvol, live?.Strength,
                tickCost, live?.SpreadTicks, vwapDist, live?.RangePosition, score, string.Join(" ", tags)));
        }

        if (closingMode)
        {
            // 조건을 모두 통과한 종목 → 통과 개수 → 점수 순
            return result
                .OrderByDescending(c => c.ClosingPassed == c.ClosingTotal && c.ClosingTotal >= 4)
                .ThenByDescending(c => c.ClosingPassed - (c.ClosingTotal - c.ClosingPassed) * 2)
                .ThenByDescending(c => c.Score)
                .Take(s.MaxCandidates).ToList();
        }
        return result.OrderByDescending(c => c.Score).Take(s.MaxCandidates).ToList();
    }

    /// <summary>점수 0~100 (설계 문서 5.4 초기안)</summary>
    public static decimal Score(decimal? rvol, decimal amount, decimal? strength, decimal? vwapDistPct, decimal? rangePos, decimal tickCostPct, ScannerSettings s)
    {
        var rv = rvol is { } r ? Math.Clamp(r / 10m, 0, 1) : 0.4m;
        var am = amount > 0 ? Math.Clamp(((decimal)Math.Log10((double)amount) - 9m) / 2m, 0, 1) : 0;   // 10억→0, 1000억→1
        var st = strength is { } x ? Math.Clamp((x - 80m) / 70m, 0, 1) : 0.5m;
        var vw = vwapDistPct switch { null => 0.5m, < 0 => 0m, <= 3m => 1m, _ => 0.5m };
        var hi = rangePos ?? 0.5m;
        var cost = s.MaxTickCostPct > 0 ? Math.Clamp(tickCostPct / s.MaxTickCostPct, 0, 1) : 0;
        var score = 0.30m * rv + 0.20m * am + 0.15m * st + 0.15m * vw + 0.10m * hi - 0.10m * cost;
        return Math.Round(Math.Clamp(score / 0.9m, 0, 1) * 100m, 1);
    }

    // ================================================================ 종가매매 평가

    /// <summary>당일 분봉 요약: 고가·저가·VWAP·30분 전 종가</summary>
    public sealed record IntradayStats(decimal High, decimal Low, decimal Vwap, decimal? Close30mAgo, DateTimeOffset FetchedAt, DateOnly SessionDate);

    public static IntradayStats? ComputeIntraday(IReadOnlyList<Bar> bars, DateTimeOffset now)
    {
        if (bars.Count == 0) return null;
        decimal hi = 0, lo = decimal.MaxValue, vol = 0, val = 0;
        foreach (var b in bars)
        {
            hi = Math.Max(hi, b.High);
            lo = Math.Min(lo, b.Low);
            vol += b.Volume;
            val += b.Value > 0 ? b.Value : b.TypicalPrice * b.Volume;
        }
        // 장이 끝났거나 휴장일이면 "지금"이 아니라 마지막 봉 기준으로 30분 전을 찾는다
        var lastEnd = bars[^1].Start.AddMinutes(1);
        var reference = lastEnd < now ? lastEnd : now;
        var cutoff = reference.AddMinutes(-30);
        var ago = bars.LastOrDefault(b => b.Start <= cutoff);
        return new IntradayStats(hi, lo, vol > 0 ? val / vol : bars[^1].Close, ago?.Close, now, Kst.DateOf(bars[^1].Start));
    }

    public static decimal? RangePositionOf(decimal price, IntradayStats? s)
    {
        if (s is null) return null;
        var hi = Math.Max(s.High, price);
        var lo = Math.Min(s.Low, price);
        return hi > lo ? (price - lo) / (hi - lo) : null;
    }

    /// <summary>종가매매 평가 결과. Checks 예: "등락✔ 고가권✔ VWAP✔ 30분✖ 강도? 상한가✔"</summary>
    public sealed record ClosingEvaluation(string Checks, int Passed, int Total, decimal Score, bool AllPassed);

    /// <summary>
    /// 봇의 종가베팅 매수 조건(ClosingBetSignal)과 같은 기준으로 후보를 평가한다.
    /// 값이 없는 항목(?)은 통과/실패로 세지 않는다. 점수 0~100, 실패 항목 1개당 −10.
    /// </summary>
    public static ClosingEvaluation EvaluateClosing(
        decimal changePct, decimal? rangePos, decimal? vwapDistPct, decimal? trend30mPct, decimal? strength,
        decimal tradingAmount, ScannerSettings s)
    {
        var checks = new (string Label, bool? Ok)[]
        {
            ("등락", changePct >= s.ClosingMinChangePct && changePct <= s.ClosingMaxChangePct),
            ("고가권", rangePos is { } rp ? rp >= s.ClosingMinRangePosition : null),
            ("VWAP", vwapDistPct is { } vd ? vd >= 0 : null),
            ("30분", trend30mPct is { } tr ? tr >= 0 : null),
            ("강도", strength is { } st ? st >= 100m : null),
            ("상한가", changePct < 27m), // 상한가(+30%) 3% 이내 제외
        };
        var passed = checks.Count(c => c.Ok == true);
        var failed = checks.Count(c => c.Ok == false);
        var text = string.Join(" ", checks.Select(c => c.Label + (c.Ok switch { true => "✔", false => "✖", _ => "?" })));

        var rpScore = rangePos ?? 0.5m;
        var vwScore = vwapDistPct switch { null => 0.5m, < 0 => 0m, <= 3m => 1m, <= 6m => 0.6m, _ => 0.3m };
        var trScore = trend30mPct is { } t ? Math.Clamp((t + 1m) / 3m, 0, 1) : 0.5m;
        var amScore = tradingAmount > 0 ? Math.Clamp(((decimal)Math.Log10((double)tradingAmount) - 9m) / 2m, 0, 1) : 0;
        var stScore = strength is { } x ? Math.Clamp((x - 80m) / 70m, 0, 1) : 0.5m;
        var score = (0.30m * rpScore + 0.20m * vwScore + 0.20m * trScore + 0.15m * amScore + 0.15m * stScore) * 100m - failed * 10m;

        var allPassed = failed == 0 && rangePos is not null && vwapDistPct is not null;
        return new ClosingEvaluation(text, passed, passed + failed, Math.Round(Math.Clamp(score, 0, 100), 1), allPassed);
    }

    /// <summary>장중 누적 거래량 비율 추정 (장 초반에 몰리는 형태)</summary>
    public static decimal ExpectedVolumeFraction(DateTimeOffset now)
    {
        var minutes = (Kst.TimeOf(now) - Kst.MarketOpen).TotalMinutes;
        if (Kst.TimeOf(now) < Kst.MarketOpen) minutes = 0;
        var f = Math.Pow(Math.Clamp(minutes / 390.0, 0, 1), 0.6);
        return (decimal)Math.Clamp(f, 0.03, 1.0);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* 종료 */ }
        }
    }
}
