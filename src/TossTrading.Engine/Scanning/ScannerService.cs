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
        var pre = new List<(RankingEntry E, decimal ChangePct, decimal TickCostPct)>();
        foreach (var e in merged.Values)
        {
            var change = (e.BasePrice > 0 ? e.LastPrice / e.BasePrice - 1m : e.ChangeRate ?? 0) * 100m;
            var tickCost = TickRules.TickCostRate(e.LastPrice) * 100m;
            if (e.LastPrice < s.MinPrice) continue;
            if (change < s.MinChangePct || change > s.MaxChangePct) continue;
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
                if (rvol < s.MinRvol) continue;
            }
            else tags.Add("RVOL?");

            var live = _live(e.Symbol);
            if (live?.SpreadTicks is { } spread && spread > s.MaxSpreadTicks) continue;
            decimal? vwapDist = live is { Vwap: > 0 } ? (live.LastPrice / live.Vwap - 1m) * 100m : null;

            if (change >= 15m) tags.Add("과열근접");
            if (vwapDist is { } vd && vd is >= 0 and <= 1m) tags.Add("VWAP근접");
            if (live?.RangePosition is >= 0.95m) tags.Add("신고가근접");

            var score = Score(rvol, e.TradingAmount, live?.Strength, vwapDist, live?.RangePosition, tickCost, s);
            var name = _stocks.TryGetValue(e.Symbol, out var st) ? st.Name : e.Symbol;
            result.Add(new ScanCandidate(e.Symbol, name, e.LastPrice, change, e.TradingAmount, rvol, live?.Strength,
                tickCost, live?.SpreadTicks, vwapDist, live?.RangePosition, score, string.Join(" ", tags)));
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
