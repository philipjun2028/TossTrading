using TossTrading.Domain;
using TossTrading.Engine.Paper;

namespace TossTrading.Engine.Backtest;

/// <summary>백테스트 설정. 봇·리스크·스캐너·비용 설정은 실매매와 같은 것을 그대로 쓴다.</summary>
public sealed class BacktestOptions
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public decimal StartingCash { get; set; } = 10_000_000m;

    /// <summary>자동 운용 설정 (백테스트는 항상 자동 운용으로 종목을 고른다)</summary>
    public AutoPilotPlan Plan { get; set; } = AutoPilotPlan.Default(enabled: true);

    public RiskSettings Risk { get; set; } = new();
    public ScannerSettings Scanner { get; set; } = new();
    public CostSettings Cost { get; set; } = new();

    /// <summary>하루에 분봉을 받아 재생할 최대 종목 수 (그날 고가 등락률·거래대금 조건을 넘은 종목 중 거래대금 상위)</summary>
    public int MaxSymbolsPerDay { get; set; } = 40;

    /// <summary>스캐너 실행 간격 (가상 시간, 분)</summary>
    public int ScanIntervalMinutes { get; set; } = 1;

    /// <summary>순위 종목 외에 추가로 포함할 종목 코드</summary>
    public List<string> ExtraSymbols { get; set; } = new();

    /// <summary>결과·로그·분석 기록을 저장할 폴더 (null 이면 저장 안 함)</summary>
    public string? OutputDirectory { get; set; }
}

public sealed record BacktestProgress(string Stage, int Done, int Total, string Message)
{
    public double Percent => Total > 0 ? Math.Clamp(Done * 100.0 / Total, 0, 100) : 0;
}

public sealed record BacktestDay(
    DateOnly Date, int Symbols, int Trades, int Wins, decimal RealizedNet, decimal EquityEnd, decimal DayReturnPct,
    decimal CumulativeReturnPct, int OpenPositions, string? Note);

public sealed record OpenPositionView(string Symbol, string Name, decimal Quantity, decimal AveragePrice, decimal LastPrice, decimal UnrealizedNet);

public sealed record BacktestResult(
    DateOnly From,
    DateOnly To,
    string DataName,
    decimal StartingCash,
    decimal EndingEquity,
    decimal NetProfit,
    decimal ReturnPct,
    decimal RealizedNet,
    decimal MaxDrawdownPct,
    IReadOnlyList<BacktestDay> Days,
    IReadOnlyList<ClosedTrade> Trades,
    IReadOnlyList<OpenPositionView> OpenPositions,
    IReadOnlyList<string> Warnings,
    string? OutputDirectory,
    TimeSpan Elapsed,
    bool Canceled);

/// <summary>
/// 백테스트 실행기: 과거 데이터를 하루씩 ReplayMarket 으로 재생하면서 실제 TradingEngine(자동 운용·봇·리스크·모의체결)을 돌린다.
///
/// 날짜 루프:
///   1) 그날 "후보가 될 수 있었던" 종목 선별 — 일봉 고가 기준 등락률·거래대금 조건 통과 (필요조건이라 미래 정보로 좋은 종목만 고르는 효과는 없음)
///   2) 선별 종목 + 전날부터 보유 중인 종목의 1분봉 적재
///   3) 09:00~15:30 을 15초 단위로 진행: 체결 발생 → 엔진 처리 완료 대기 → 타이머 → (1분마다) 스캐너
///   4) 장 마감 후 평가금액·손익 기록 → 다음 거래일
/// </summary>
public sealed class BacktestRunner
{
    private readonly IHistoryProvider _history;
    private readonly BacktestOptions _o;
    private readonly IProgress<BacktestProgress>? _progress;
    private readonly List<string> _warnings = new();

    public BacktestRunner(IHistoryProvider history, BacktestOptions options, IProgress<BacktestProgress>? progress = null)
    {
        _history = history;
        _o = options;
        _progress = progress;
    }

    public async Task<BacktestResult> RunAsync(CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        if (_o.To < _o.From) throw new ArgumentException("종료일이 시작일보다 빠릅니다.");
        var planErrors = _o.Plan.Settings.Validate();
        if (planErrors.Count > 0) throw new ArgumentException(string.Join("\n", planErrors));

        // ---- 1. 종목 풀 + 일봉 ----
        Report("종목 목록", 0, 1, "대상 종목 조회 중");
        var infos = new Dictionary<string, StockInfo>();
        foreach (var i in await _history.GetUniverseAsync(ct).ConfigureAwait(false))
        {
            if (i.TradingSuspended || i.LiquidationTrading) continue;
            if (_o.Scanner.ExcludeNonCommonStock && (i.SecurityType != "STOCK" || !i.IsCommonShare)) continue;
            infos[i.Symbol] = i;
        }
        foreach (var sym in _o.ExtraSymbols.Select(x => x.Trim()).Where(x => x.Length > 0))
            infos.TryAdd(sym, new StockInfo(sym, sym, "", "STOCK", true, false, false));
        if (infos.Count == 0) throw new InvalidOperationException("백테스트 대상 종목이 없습니다.");

        var dailyCount = _o.To.DayNumber - _o.From.DayNumber + 40; // 기간 + RVOL 계산용 20거래일 여유
        var daily = new Dictionary<string, IReadOnlyList<Bar>>();
        var n = 0;
        foreach (var sym in infos.Keys.ToList())
        {
            ct.ThrowIfCancellationRequested();
            Report("일봉", ++n, infos.Count, $"{infos[sym].Name} 일봉");
            try
            {
                var bars = await _history.GetDailyBarsAsync(sym, _o.To, dailyCount, ct).ConfigureAwait(false);
                if (bars.Count > 0) daily[sym] = bars;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Warn($"{sym} 일봉 조회 실패: {ex.Message}");
            }
        }

        var days = daily.Values.SelectMany(b => b).Select(b => Kst.DateOf(b.Start))
            .Where(d => d >= _o.From && d <= _o.To).Distinct().OrderBy(d => d).ToList();
        if (days.Count == 0) throw new InvalidOperationException("기간 안에 거래일 데이터가 없습니다 (일봉 없음).");

        // ---- 2. 엔진 준비 ----
        var replay = new ReplayMarket(Kst.At(days[0], new TimeOnly(8, 30)));
        var options = new EngineOptions
        {
            Execution = ExecutionMode.Paper,
            DataSource = DataSourceKind.Backtest,
            Risk = _o.Risk.Clone(),
            Scanner = _o.Scanner.Clone(),
            Cost = _o.Cost.Clone(),
            AutoPilot = _o.Plan with { Settings = EnableCopy(_o.Plan.Settings) },
            RunScanner = false,
            DataDirectory = _o.OutputDirectory,
            RecordTicks = false,
            OrderRatePerSecond = 100_000,
            OpeningOrderRatePerSecond = 100_000,
        };
        var paper = new PaperBroker(_o.StartingCash, new CostModel(options.Cost), replay);
        foreach (var i in infos.Values) paper.SetName(i.Symbol, i.Name);

        var results = new List<BacktestDay>();
        var equityPeak = _o.StartingCash;
        var maxDd = 0m;
        var prevEquity = _o.StartingCash;
        var canceled = false;
        IReadOnlyList<ClosedTrade> trades = Array.Empty<ClosedTrade>();
        var open = new List<OpenPositionView>();
        decimal endEquity;

        await using (var engine = new TradingEngine(options, replay, replay, paper, replay))
        {
            // 엔진 수명은 취소 토큰과 분리한다 (취소해도 그때까지 결과를 정리해 돌려주기 위해). 취소는 단계 사이에서만 확인.
            var none = CancellationToken.None;
            await engine.StartAsync(none).ConfigureAwait(false);
            await engine.SettleAsync(none).ConfigureAwait(false);

            for (var di = 0; di < days.Count; di++)
            {
                var date = days[di];
                if (ct.IsCancellationRequested) { canceled = true; break; }

                // ---- 3. 그날 재생할 종목 ----
                var held = engine.Snapshot.Bots.Where(b => b.Quantity > 0).Select(b => b.Symbol).ToHashSet();
                var picks = PickSymbols(date, daily).Union(held).ToList();
                var minute = new Dictionary<string, IReadOnlyList<Bar>>();
                var k = 0;
                foreach (var sym in picks)
                {
                    if (ct.IsCancellationRequested) break;
                    Report("분봉", di * 1000 + ++k, days.Count * 1000, $"{date:MM/dd} {Name(infos, sym)} 분봉 ({k}/{picks.Count})");
                    try
                    {
                        var bars = await _history.GetMinuteBarsAsync(sym, date, ct).ConfigureAwait(false);
                        if (bars.Count > 0) minute[sym] = bars;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Warn($"{date:MM/dd} {sym} 분봉 조회 실패: {ex.Message}");
                    }
                }
                if (ct.IsCancellationRequested) { canceled = true; break; }

                string? note = null;
                if (minute.Count == 0)
                {
                    note = picks.Count == 0 ? "조건 맞는 종목 없음" : "분봉 데이터 없음";
                    if (picks.Count > 0) Warn($"{date:yyyy-MM-dd}: 분봉 데이터가 없어 건너뜀 (API 가 오래된 분봉을 주지 않는 경우)");
                }
                else if (held.Any(h => !minute.ContainsKey(h)))
                {
                    note = "보유 종목 분봉 없음";
                    Warn($"{date:yyyy-MM-dd}: 보유 종목 {string.Join(",", held.Where(h => !minute.ContainsKey(h)))} 분봉 없음 → 그날 매도 불가");
                }

                var dailyBefore = daily.ToDictionary(kv => kv.Key,
                    kv => (IReadOnlyList<Bar>)kv.Value.Where(b => Kst.DateOf(b.Start) < date).ToList());
                replay.BeginDay(date, minute, dailyBefore, infos);

                var tradesBefore = engine.Snapshot.Trades.Count;
                Report("재생", di, days.Count, $"{date:yyyy-MM-dd} 재생 중 ({minute.Count}종목)");

                // 장 시작 전: 날짜 변경 처리
                await StepAsync(engine, replay, Kst.At(date, new TimeOnly(8, 59))).ConfigureAwait(false);

                if (minute.Count > 0)
                {
                    var first = Kst.At(date, Kst.MarketOpen);
                    for (var m = 0; m <= 390; m++)
                    {
                        if (ct.IsCancellationRequested) { canceled = true; break; }
                        var minuteStart = first.AddMinutes(m);
                        for (var phase = 0; phase < ReplayMarket.PhasesPerMinute; phase++)
                        {
                            var t = minuteStart.AddSeconds(phase * ReplayMarket.PhaseSeconds);
                            replay.AdvanceTo(t);
                            replay.EmitPhase(minuteStart, phase);
                            await StepAsync(engine, replay, t).ConfigureAwait(false);
                            if (phase == 0 && m % Math.Max(1, _o.ScanIntervalMinutes) == 0)
                            {
                                await engine.ScanNowAsync(none).ConfigureAwait(false);
                                await engine.SettleAsync(none).ConfigureAwait(false);
                            }
                        }
                        if (m % 5 == 0)
                        {
                            var eq = (await paper.GetAccountSnapshotAsync(none).ConfigureAwait(false)).Equity;
                            equityPeak = Math.Max(equityPeak, eq);
                            if (equityPeak > 0) maxDd = Math.Max(maxDd, (equityPeak - eq) / equityPeak * 100m);
                        }
                    }
                    if (canceled) break;
                }

                // 장 마감 후 정리
                await StepAsync(engine, replay, Kst.At(date, new TimeOnly(15, 35))).ConfigureAwait(false);
                var snap = engine.Snapshot;
                var dayTrades = snap.Trades.Skip(tradesBefore).ToList();
                var equity = (await paper.GetAccountSnapshotAsync(none).ConfigureAwait(false)).Equity;
                equityPeak = Math.Max(equityPeak, equity);
                if (equityPeak > 0) maxDd = Math.Max(maxDd, (equityPeak - equity) / equityPeak * 100m);
                results.Add(new BacktestDay(date, minute.Count, dayTrades.Count, dayTrades.Count(t => t.NetPnl > 0),
                    Math.Round(dayTrades.Sum(t => t.NetPnl), 0), Math.Round(equity, 0),
                    prevEquity > 0 ? Math.Round((equity / prevEquity - 1m) * 100m, 3) : 0,
                    Math.Round((equity / _o.StartingCash - 1m) * 100m, 3),
                    snap.Bots.Count(b => b.Quantity > 0), note));
                prevEquity = equity;
                Report("재생", di + 1, days.Count, $"{date:yyyy-MM-dd} 완료 · 누적 {(equity / _o.StartingCash - 1m) * 100m:+0.00;-0.00}%");
            }

            await engine.TickAsync().ConfigureAwait(false);
            var final = engine.Snapshot;
            trades = final.Trades.ToList();
            open = final.Bots.Where(b => b.Quantity > 0)
                .Select(b => new OpenPositionView(b.Symbol, b.Name, b.Quantity, b.AveragePrice, b.LastPrice, Math.Round(b.UnrealizedNet, 0)))
                .ToList();
            endEquity = (await paper.GetAccountSnapshotAsync(none).ConfigureAwait(false)).Equity;
        }

        if (open.Count > 0) Warn($"기간 종료 시 보유 {open.Count}종목은 마지막 가격으로 평가했습니다 (미실현).");
        var net = endEquity - _o.StartingCash;
        return new BacktestResult(_o.From, _o.To, _history.Name, _o.StartingCash, Math.Round(endEquity, 0), Math.Round(net, 0),
            _o.StartingCash > 0 ? Math.Round(net / _o.StartingCash * 100m, 3) : 0,
            Math.Round(trades.Sum(t => t.NetPnl), 0), Math.Round(maxDd, 3),
            results, trades, open, _warnings.ToList(), _o.OutputDirectory, DateTime.UtcNow - started, canceled);
    }

    /// <summary>
    /// 그날 스캐너 후보가 "될 수 있었던" 종목: 고가 기준 등락률이 하한 이상이고 거래대금이 충분한 종목.
    /// 종가·등락 결과가 아니라 필요조건만 보므로, 장중에 실제로 어떻게 보였는지는 재생에서 판단된다.
    /// </summary>
    private IEnumerable<string> PickSymbols(DateOnly date, Dictionary<string, IReadOnlyList<Bar>> daily)
    {
        var s = _o.Scanner;
        var minChange = Math.Min(s.MinChangePct, s.ClosingMinChangePct);
        var minAmount = s.MinTradingAmount * 0.3m;
        var list = new List<(string Sym, decimal Amount)>();
        foreach (var (sym, bars) in daily)
        {
            var idx = -1;
            for (var i = 0; i < bars.Count; i++) if (Kst.DateOf(bars[i].Start) == date) { idx = i; break; }
            if (idx <= 0) continue;
            var today = bars[idx];
            var prev = bars[idx - 1].Close;
            if (prev <= 0 || today.High < s.MinPrice) continue;
            if ((today.High / prev - 1m) * 100m < minChange) continue;
            var amount = today.Value > 0 ? today.Value : today.Close * today.Volume;
            if (amount < minAmount) continue;
            list.Add((sym, amount));
        }
        return list.OrderByDescending(x => x.Amount).Take(Math.Max(1, _o.MaxSymbolsPerDay)).Select(x => x.Sym);
    }

    private static async Task StepAsync(TradingEngine engine, ReplayMarket replay, DateTimeOffset t)
    {
        replay.AdvanceTo(t);
        await engine.SettleAsync().ConfigureAwait(false);
        await engine.TickAsync().ConfigureAwait(false);
        await engine.SettleAsync().ConfigureAwait(false);
    }

    private static AutoPilotSettings EnableCopy(AutoPilotSettings s)
    {
        var c = s.Clone();
        c.Enabled = true;
        return c;
    }

    private static string Name(Dictionary<string, StockInfo> infos, string sym) => infos.TryGetValue(sym, out var i) ? i.Name : sym;

    private void Report(string stage, int done, int total, string message) => _progress?.Report(new BacktestProgress(stage, done, total, message));

    private void Warn(string message)
    {
        if (_warnings.Count < 200) _warnings.Add(message);
    }
}
