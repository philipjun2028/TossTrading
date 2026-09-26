using System.Globalization;
using System.Text;
using System.Text.Json;
using TossTrading.Domain;

namespace TossTrading.Engine.Analytics;

/// <summary>리포트 대상 필터</summary>
public sealed record ReportFilter(DateOnly From, DateOnly To, DataSourceKind? DataSource = null, ExecutionMode? Execution = null, string? Strategy = null)
{
    public static ReportFilter LastDays(int days, DateOnly today) => new(today.AddDays(-(Math.Max(1, days) - 1)), today);
}

/// <summary>analysis_*.jsonl 에서 읽어 들인 기록 모음</summary>
public sealed class AnalysisDataSet
{
    public List<TradeAnalysisRecord> Trades { get; } = new();
    public List<SignalRecord> Signals { get; } = new();
    public Dictionary<string, SignalDecisionRecord> Decisions { get; } = new();
    public Dictionary<string, FollowUpRecord> FollowUps { get; } = new();
    public int BadLines { get; set; }

    public static AnalysisDataSet Load(string journalDirectory, ReportFilter filter)
    {
        var ds = new AnalysisDataSet();
        if (!Directory.Exists(journalDirectory)) return ds;
        foreach (var file in Directory.EnumerateFiles(journalDirectory, "analysis_*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["analysis_".Length..];
            if (!DateOnly.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            // 추적 기록은 기준 시점 날짜 파일에 쌓이므로 범위를 하루 넓혀 읽는다
            if (date < filter.From || date > filter.To.AddDays(1)) continue;
            IEnumerable<string> lines;
            try { lines = ReadLinesShared(file); }
            catch (IOException) { continue; }
            foreach (var line in lines) ds.AddLine(line);
        }
        ds.Apply(filter);
        return ds;
    }

    public void AddLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var kind = doc.RootElement.GetProperty("kind").GetString();
            var data = doc.RootElement.GetProperty("data");
            var o = AnalyticsRecorder.Json;
            switch (kind)
            {
                case "trade": Trades.Add(data.Deserialize<TradeAnalysisRecord>(o)!); break;
                case "signal": Signals.Add(data.Deserialize<SignalRecord>(o)!); break;
                case "decision": { var d = data.Deserialize<SignalDecisionRecord>(o)!; Decisions[d.SignalId] = d; break; }
                case "followup": { var f = data.Deserialize<FollowUpRecord>(o)!; FollowUps[f.RefId] = f; break; }
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            BadLines++;
        }
    }

    public void Apply(ReportFilter f)
    {
        bool Keep(DateTimeOffset t, DataSourceKind src, ExecutionMode ex, string strategy) =>
            Kst.DateOf(t) >= f.From && Kst.DateOf(t) <= f.To
            && (f.DataSource is null || f.DataSource == src)
            && (f.Execution is null || f.Execution == ex)
            && (f.Strategy is null || f.Strategy == strategy);
        Trades.RemoveAll(t => !Keep(t.ExitTime, t.DataSource, t.Execution, t.Strategy));
        Signals.RemoveAll(s => !Keep(s.Time, s.DataSource, s.Execution, s.Strategy));
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        // 엔진이 쓰는 중에도 읽을 수 있도록 공유 모드로 연다
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        var list = new List<string>();
        while (sr.ReadLine() is { } l) list.Add(l);
        return list;
    }
}

/// <summary>거래 묶음 통계</summary>
public sealed record GroupStats(string Key, int Count, decimal WinRatePct, decimal AvgNetPct, decimal AvgWinPct, decimal AvgLossPct,
    decimal? ProfitFactor, decimal TotalNet, decimal? AvgR, decimal AvgMfePct, decimal AvgMaePct, double AvgHoldMinutes)
{
    public static GroupStats Of(string key, IReadOnlyCollection<TradeAnalysisRecord> trades)
    {
        if (trades.Count == 0) return new GroupStats(key, 0, 0, 0, 0, 0, null, 0, null, 0, 0, 0);
        var wins = trades.Where(t => t.NetPnl > 0).ToList();
        var losses = trades.Where(t => t.NetPnl <= 0).ToList();
        var grossWin = wins.Sum(t => t.NetPnl);
        var grossLoss = -losses.Sum(t => t.NetPnl);
        var rs = trades.Where(t => t.RMultiple is not null).Select(t => t.RMultiple!.Value).ToList();
        return new GroupStats(key, trades.Count,
            Math.Round(wins.Count * 100m / trades.Count, 1),
            Math.Round(trades.Average(t => t.NetPct), 3),
            wins.Count > 0 ? Math.Round(wins.Average(t => t.NetPct), 3) : 0,
            losses.Count > 0 ? Math.Round(losses.Average(t => t.NetPct), 3) : 0,
            grossLoss > 0 ? Math.Round(grossWin / grossLoss, 2) : null,
            trades.Sum(t => t.NetPnl),
            rs.Count > 0 ? Math.Round(rs.Average(), 2) : null,
            Math.Round(trades.Average(t => t.MfePct), 3),
            Math.Round(trades.Average(t => t.MaePct), 3),
            Math.Round(trades.Average(t => t.HoldMinutes), 1));
    }
}

/// <summary>
/// 저장된 분석 기록으로 성과 리포트(마크다운)와 개선 제안을 만든다.
/// 표본이 적은 구간은 제안에서 제외한다 (MinSample).
/// </summary>
public sealed class PerformanceReport
{
    public const int MinSample = 5;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly AnalysisDataSet _ds;
    private readonly ReportFilter _filter;
    private readonly List<string> _suggestions = new();

    public PerformanceReport(AnalysisDataSet ds, ReportFilter filter)
    {
        _ds = ds;
        _filter = filter;
    }

    public IReadOnlyList<string> Suggestions => _suggestions;

    public string BuildMarkdown()
    {
        _suggestions.Clear();
        var sb = new StringBuilder();
        var trades = _ds.Trades.OrderBy(t => t.ExitTime).ToList();
        sb.AppendLine($"# 매매 성과 분석 ({_filter.From:yyyy-MM-dd} ~ {_filter.To:yyyy-MM-dd})");
        var scope = new List<string>();
        if (_filter.DataSource is { } src) scope.Add(src == DataSourceKind.Toss ? "토스 실시간" : "시뮬레이션");
        if (_filter.Execution is { } ex) scope.Add(ex == ExecutionMode.Live ? "실전" : "모의");
        if (_filter.Strategy is { } st) scope.Add(st);
        sb.AppendLine($"대상: {(scope.Count > 0 ? string.Join(", ", scope) : "전체")} · 거래 {trades.Count}건 · 신호 {_ds.Signals.Count}건");
        sb.AppendLine("수익률은 모두 수수료·세금 차감 후(순) 기준, MFE/MAE 는 보유 중 최고/최저가의 매수가 대비 %.");
        sb.AppendLine();

        if (trades.Count == 0)
        {
            sb.AppendLine("분석할 거래가 없습니다. 모의/실전 매매가 쌓이면 다시 실행하세요.");
            AppendSignals(sb);
            return sb.ToString();
        }

        AppendSummary(sb, trades);
        AppendGroup(sb, "전략별", trades, t => t.Strategy);
        AppendGroup(sb, "청산 사유별", trades, t => ExitName(t.FinalExitKind));
        AppendGroup(sb, "진입 시간대별", trades, t => TimeBucket(t.EntryTime), suggest: true, feature: "진입 시간대");
        AppendGroup(sb, "진입 시 등락률", trades, t => Bucket(t.EntryContext?.ChangePct, new[] { 3m, 6m, 10m, 15m, 20m }, "%"), suggest: true, feature: "등락률");
        AppendGroup(sb, "진입 시 VWAP 이격", trades, t => Bucket(t.EntryContext?.VwapDistPct, new[] { 0m, 1m, 2m, 4m }, "%"), suggest: true, feature: "VWAP 이격");
        AppendGroup(sb, "진입 시 당일 범위 위치", trades, t => Bucket(t.EntryContext?.RangePosition * 100m, new[] { 50m, 70m, 85m, 95m }, "%"), suggest: true, feature: "당일 범위 위치");
        AppendGroup(sb, "진입 시 체결강도", trades, t => Bucket(t.EntryContext?.Strength, new[] { 80m, 100m, 130m, 180m }, ""), suggest: true, feature: "체결강도");
        AppendGroup(sb, "진입 시 거래량 배수(직전 10분 평균 대비)", trades, t => Bucket(t.EntryContext?.VolumeRatio, new[] { 1m, 2m, 4m }, "배"), suggest: true, feature: "거래량 배수");
        AppendGroup(sb, "진입 시 스프레드(틱)", trades, t => t.EntryContext?.SpreadTicks is { } s ? (s >= 3 ? "3틱 이상" : $"{s}틱") : "(없음)", suggest: true, feature: "스프레드");
        AppendExcursion(sb, trades);
        AppendAfterExit(sb, trades);
        AppendSlippage(sb, trades);
        AppendSignals(sb);
        AppendSuggestions(sb);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 섹션

    private void AppendSummary(StringBuilder sb, List<TradeAnalysisRecord> trades)
    {
        var g = GroupStats.Of("전체", trades);
        var payoff = g.AvgLossPct != 0 ? Math.Abs(g.AvgWinPct / g.AvgLossPct) : (decimal?)null;
        sb.AppendLine("## 1. 요약");
        sb.AppendLine("| 항목 | 값 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 거래 수 | {g.Count} |");
        sb.AppendLine($"| 승률 | {g.WinRatePct:F1}% |");
        sb.AppendLine($"| 거래당 기대값(평균 순수익률) | {Signed(g.AvgNetPct)}% |");
        sb.AppendLine($"| 평균 이익 / 평균 손실 | {Signed(g.AvgWinPct)}% / {Signed(g.AvgLossPct)}% (손익비 {Fmt(payoff)}) |");
        sb.AppendLine($"| Profit Factor | {Fmt(g.ProfitFactor)} |");
        sb.AppendLine($"| 총 순손익 | {g.TotalNet:+#,0;-#,0;0}원 |");
        sb.AppendLine($"| 평균 R | {Fmt(g.AvgR)} |");
        sb.AppendLine($"| 평균 보유 시간 | {g.AvgHoldMinutes:F1}분 |");
        var worst = trades.OrderBy(t => t.NetPnl).First();
        sb.AppendLine($"| 최대 손실 거래 | {worst.Name} {Signed(worst.NetPct)}% ({worst.NetPnl:+#,0;-#,0}원, {ExitName(worst.FinalExitKind)}) |");
        var maxStreak = 0; var cur = 0;
        foreach (var t in trades) { cur = t.NetPnl <= 0 ? cur + 1 : 0; maxStreak = Math.Max(maxStreak, cur); }
        sb.AppendLine($"| 최대 연속 손실 | {maxStreak}회 |");
        sb.AppendLine();

        if (g.Count >= MinSample)
        {
            if (g.AvgNetPct < 0 && g.WinRatePct >= 50)
                Suggest($"승률 {g.WinRatePct:F0}% 인데 기대값이 음수입니다 → 평균 손실({g.AvgLossPct:F2}%)이 평균 이익({g.AvgWinPct:F2}%)보다 큽니다. 손절 폭을 줄이거나 익절 목표를 올려 손익비를 개선하세요.");
            else if (g.AvgNetPct < 0 && g.WinRatePct < 40)
                Suggest($"승률 {g.WinRatePct:F0}% 로 낮습니다 → 진입 필터를 강화하세요 (아래 '진입 조건별' 표에서 기대값이 음수인 구간을 제외).");
        }
    }

    private void AppendGroup(StringBuilder sb, string title, List<TradeAnalysisRecord> trades, Func<TradeAnalysisRecord, string> keyOf,
        bool suggest = false, string? feature = null)
    {
        var groups = trades.GroupBy(keyOf).Select(gr => GroupStats.Of(gr.Key, gr.ToList())).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        sb.AppendLine($"## {title}");
        sb.AppendLine("| 구분 | 거래 | 승률 | 기대값 | 평균이익 | 평균손실 | PF | 순손익(원) | MFE | MAE |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var g in groups)
            sb.AppendLine($"| {g.Key} | {g.Count} | {g.WinRatePct:F0}% | {Signed(g.AvgNetPct)}% | {Signed(g.AvgWinPct)}% | {Signed(g.AvgLossPct)}% | {Fmt(g.ProfitFactor)} | {g.TotalNet:+#,0;-#,0;0} | {g.AvgMfePct:F2}% | {g.AvgMaePct:F2}% |");
        sb.AppendLine();

        if (!suggest || feature is null || groups.Count < 2) return;
        var overall = trades.Average(t => t.NetPct);
        foreach (var g in groups.Where(g => g.Count >= MinSample && g.Key != "(없음)"))
        {
            if (g.AvgNetPct < 0 && g.AvgNetPct < overall - 0.2m)
                Suggest($"{feature} '{g.Key}' 구간 진입은 기대값 {Signed(g.AvgNetPct)}% ({g.Count}건, 승률 {g.WinRatePct:F0}%) → 이 구간을 진입 필터로 제외하는 것을 검토하세요.");
        }
        var best = groups.Where(g => g.Count >= MinSample && g.Key != "(없음)").OrderByDescending(g => g.AvgNetPct).FirstOrDefault();
        if (best is not null && best.AvgNetPct > 0 && best.AvgNetPct > overall + 0.3m)
            Suggest($"{feature} '{best.Key}' 구간 성과가 가장 좋습니다 (기대값 {Signed(best.AvgNetPct)}%, {best.Count}건) → 이 구간에 비중을 늘리거나 조건을 좁혀보세요.");
    }

    private void AppendExcursion(StringBuilder sb, List<TradeAnalysisRecord> trades)
    {
        sb.AppendLine("## 보유 중 흐름 (MFE/MAE) — 손절·익절 폭 점검");
        var winners = trades.Where(t => t.NetPnl > 0).ToList();
        var losers = trades.Where(t => t.NetPnl <= 0).ToList();

        // 이익 반납: 한때 +1R(또는 +1%) 이상 갔다가 손실로 끝난 거래
        var givebacks = losers.Where(t => t.MfePct >= Math.Max(1m, t.RiskPct)).ToList();
        sb.AppendLine($"- 손실 거래 {losers.Count}건 중 **한때 +1R 이상 수익이었던 거래 {givebacks.Count}건**" +
                      (givebacks.Count > 0 ? $" (평균 최고 {givebacks.Average(t => t.MfePct):F2}% → 결과 {givebacks.Average(t => t.NetPct):F2}%)" : ""));
        if (losers.Count >= MinSample && givebacks.Count >= Math.Max(2, losers.Count * 0.3))
            Suggest($"손실 거래의 {givebacks.Count * 100 / losers.Count}% 가 한때 +1R 이상 수익이었습니다 → 본절 스탑 이동을 더 빨리(예: +0.7R) 하거나 1차 분할 익절 비율을 늘리세요.");

        if (winners.Count > 0)
        {
            var capture = winners.Where(t => t.MfePct > 0).Select(t => t.NetPct / t.MfePct).DefaultIfEmpty(0).Average();
            sb.AppendLine($"- 이익 거래의 **수익 포착률** (순수익 ÷ 보유 중 최고 수익) 평균 {capture * 100:F0}%");
            if (winners.Count >= MinSample && capture < 0.45m)
                Suggest($"이익 거래가 보유 중 최고 수익의 {capture * 100:F0}% 만 가져갔습니다 → 트레일링 거리(현재 설정 확인)를 좁히거나 목표 익절을 낮춰 보세요.");

            // 이익 거래가 견딘 최대 역행 → 손절 폭이 과도하게 넓은지
            var maes = winners.Select(t => -t.MaePct).OrderBy(x => x).ToList();
            var p90 = maes[(int)Math.Floor((maes.Count - 1) * 0.9)];
            var avgStop = trades.Average(t => t.RiskPct);
            sb.AppendLine($"- 이익 거래의 최대 역행(MAE) 90% 지점 {p90:F2}% · 평균 초기 손절 거리 {avgStop:F2}%");
            if (winners.Count >= MinSample && p90 > 0 && p90 < avgStop * 0.6m)
                Suggest($"이익 거래의 90% 가 {p90:F2}% 이상 역행하지 않았는데 손절 거리는 평균 {avgStop:F2}% 입니다 → 손절을 {Math.Round(p90 * 1.2m, 2)}% 근처로 좁히면 손실 거래의 손실이 줄 수 있습니다.");
        }

        var stopped = losers.Where(t => t.FinalExitKind == ExitKind.StopLoss).ToList();
        if (stopped.Count > 0)
            sb.AppendLine($"- 손절 청산 {stopped.Count}건 평균 {stopped.Average(t => t.NetPct):F2}% (평균 초기 손절 거리 {stopped.Average(t => t.RiskPct):F2}%)");
        sb.AppendLine();
    }

    private void AppendAfterExit(StringBuilder sb, List<TradeAnalysisRecord> trades)
    {
        sb.AppendLine("## 청산 이후 가격 흐름 (최대 60분 추적)");
        var rows = trades.Select(t => (Trade: t, F: _ds.FollowUps.GetValueOrDefault(t.TradeId))).Where(x => x.F is { Max60m: not null }).ToList();
        if (rows.Count == 0)
        {
            sb.AppendLine("- 추적 데이터가 아직 없습니다 (청산 후 엔진을 60분 이상 켜두면 기록됩니다).");
            sb.AppendLine();
            return;
        }

        var stops = rows.Where(x => x.Trade.FinalExitKind is ExitKind.StopLoss or ExitKind.BreakEven).ToList();
        if (stops.Count > 0)
        {
            var recovered = stops.Count(x => x.F!.Max60m >= x.Trade.AverageEntry);
            var furtherDown = stops.Count(x => x.F!.Min60m <= x.Trade.AverageExit * 0.98m);
            sb.AppendLine($"- 손절/본절 {stops.Count}건 중 60분 안에 **매수가를 회복한 경우 {recovered}건** ({recovered * 100 / stops.Count}%), 손절가보다 2% 이상 더 빠진 경우 {furtherDown}건");
            if (stops.Count >= MinSample && recovered * 100 / stops.Count >= 40)
                Suggest($"손절 후 {recovered * 100 / stops.Count}% 가 60분 안에 매수가를 회복했습니다 → 손절이 너무 타이트하거나 노이즈에 걸립니다. 손절 % 를 조금 넓히거나 구조적 손절(직전 저점)을 사용해 보세요.");
            else if (stops.Count >= MinSample && furtherDown * 100 / stops.Count >= 50)
                sb.AppendLine("  - 손절 후 추가 하락이 많아 손절 규칙이 손실 확대를 잘 막고 있습니다.");
        }

        var profits = rows.Where(x => x.Trade.FinalExitKind is ExitKind.TakeProfit or ExitKind.Trailing or ExitKind.TimeStop).ToList();
        if (profits.Count > 0)
        {
            var ranMore = profits.Count(x => x.F!.Max60m >= x.Trade.AverageExit * 1.02m);
            var avgAfter = profits.Average(x => (x.F!.Max60m!.Value / x.Trade.AverageExit - 1m) * 100m);
            sb.AppendLine($"- 익절/트레일링/타임스탑 {profits.Count}건 중 이후 **2% 이상 더 오른 경우 {ranMore}건** (청산 후 60분 최고 평균 +{avgAfter:F2}%)");
            if (profits.Count >= MinSample && ranMore * 100 / profits.Count >= 40)
                Suggest($"익절·트레일링 청산의 {ranMore * 100 / profits.Count}% 가 이후 2% 이상 더 올랐습니다 → 목표 익절을 높이거나 분할 익절 후 나머지를 트레일링으로 더 길게 가져가세요.");
        }
        sb.AppendLine();
    }

    private void AppendSlippage(StringBuilder sb, List<TradeAnalysisRecord> trades)
    {
        sb.AppendLine("## 체결 품질 (슬리피지)");
        var entry = trades.Average(t => t.EntrySlippagePct);
        var exits = trades.Where(t => t.ExitSlippagePct is not null).Select(t => t.ExitSlippagePct!.Value).ToList();
        sb.AppendLine($"- 진입: 신호 시점 가격 대비 평균 체결가 {Signed(Math.Round(entry, 3))}%");
        if (exits.Count > 0) sb.AppendLine($"- 청산: 청산 기준가(손절가·현재가) 대비 평균 체결가 {Signed(Math.Round(exits.Average(), 3))}%");
        var totalCost = trades.Sum(t => t.Costs);
        sb.AppendLine($"- 수수료·세금 합계 {totalCost:#,0}원 (총 손익 대비 비용 비중을 확인하세요)");
        if (trades.Count >= MinSample && entry > 0.3m)
            Suggest($"진입 슬리피지가 평균 +{entry:F2}% 로 큽니다 → 매수 지정가 슬리피지 틱 수를 줄이거나 스프레드가 넓은 종목을 제외하세요.");
        if (exits.Count >= MinSample && exits.Average() < -0.3m)
            Suggest($"청산 슬리피지가 평균 {exits.Average():F2}% 입니다 → 호가가 얇은 종목이 많습니다. 스캐너의 최소 거래대금을 높여 보세요.");
        sb.AppendLine();
    }

    private void AppendSignals(StringBuilder sb)
    {
        var signals = _ds.Signals;
        sb.AppendLine("## 진입 신호 처리");
        if (signals.Count == 0)
        {
            sb.AppendLine("- 기록된 신호가 없습니다.");
            sb.AppendLine();
            return;
        }
        sb.AppendLine("| 결과 | 건수 | 이후 60분 최고 | 이후 60분 최저 | 60분 후 |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var g in signals.GroupBy(DecisionOf).OrderByDescending(g => g.Count()))
        {
            var f = g.Select(s => (S: s, F: _ds.FollowUps.GetValueOrDefault(s.SignalId))).Where(x => x.F is { Max60m: not null }).ToList();
            string Avg(Func<FollowUpRecord, decimal?> sel) =>
                f.Count == 0 ? "-" : Signed(Math.Round(f.Where(x => sel(x.F!) is not null).Select(x => (sel(x.F!)!.Value / x.S.Price - 1m) * 100m).DefaultIfEmpty(0).Average(), 2)) + "%";
            sb.AppendLine($"| {g.Key} | {g.Count()} | {Avg(x => x.Max60m)} | {Avg(x => x.Min60m)} | {Avg(x => x.Price60m ?? x.SessionClose)} |");
        }
        sb.AppendLine("(진입하지 않은 신호의 이후 흐름 → 필터·리스크 한도가 좋은 기회를 막는지, 손실을 잘 피하는지 판단)");
        sb.AppendLine();

        var skipped = signals.Where(s => DecisionOf(s) != "진입")
            .Select(s => (S: s, F: _ds.FollowUps.GetValueOrDefault(s.SignalId))).Where(x => x.F is { Max60m: not null, Min60m: not null }).ToList();
        if (skipped.Count >= MinSample)
        {
            var goodMissed = skipped.Count(x => x.F!.Max60m >= x.S.Price * 1.03m && x.F.Min60m >= x.S.Price * 0.985m);
            if (goodMissed * 100 / skipped.Count >= 40)
                Suggest($"진입하지 않은 신호의 {goodMissed * 100 / skipped.Count}% 가 이후 +3% 이상 (−1.5% 이내 역행) 올랐습니다 → 진입 가능 시간·최대 진입 횟수·리스크 한도가 너무 보수적인지 검토하세요.");
        }
    }

    private string DecisionOf(SignalRecord s) =>
        _ds.Decisions.TryGetValue(s.SignalId, out var d) ? $"{s.Decision}→{d.Decision}" : s.Decision;

    private void AppendSuggestions(StringBuilder sb)
    {
        sb.AppendLine("## 개선 제안");
        if (_suggestions.Count == 0)
        {
            sb.AppendLine($"- 뚜렷한 개선 포인트가 없거나 표본이 부족합니다 (구간별 최소 {MinSample}건 필요).");
        }
        else
        {
            for (var i = 0; i < _suggestions.Count; i++) sb.AppendLine($"{i + 1}. {_suggestions[i]}");
        }
        sb.AppendLine();
        sb.AppendLine("※ 제안은 과거 기록의 통계일 뿐입니다. 설정을 바꾼 뒤에는 모의투자로 충분히 검증하고, 한 번에 하나씩 바꿔 효과를 비교하세요.");
    }

    // ------------------------------------------------------------------ CSV

    public static string TradesCsv(IEnumerable<TradeAnalysisRecord> trades)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', "거래ID", "종목코드", "종목명", "전략", "운용", "주문", "데이터", "신호시각", "진입시각", "청산시각", "보유분",
            "수량", "매수평균", "매도평균", "초기손절", "손절거리%", "순손익", "순수익률%", "R", "MFE%", "MAE%", "진입슬리피지%", "청산슬리피지%",
            "청산분류", "진입근거", "청산사유", "진입등락률%", "VWAP이격%", "범위위치", "체결강도", "ATR%", "거래량배수", "스프레드틱", "장시작후분",
            "손절설정%", "익절설정%", "트레일링발동%", "트레일링거리%", "익일보유"));
        foreach (var t in trades)
        {
            var c = t.EntryContext;
            sb.AppendLine(string.Join(',', Csv(t.TradeId), t.Symbol, Csv(t.Name), Csv(t.Strategy), t.BotMode, t.Execution, t.DataSource,
                T(t.SignalTime), T(t.EntryTime), T(t.ExitTime), N(t.HoldMinutes),
                N(t.Quantity), N(t.AverageEntry), N(t.AverageExit), N(t.InitialStop), N(t.RiskPct), N(t.NetPnl), N(t.NetPct), N(t.RMultiple),
                N(t.MfePct), N(t.MaePct), N(t.EntrySlippagePct), N(t.ExitSlippagePct),
                t.FinalExitKind, Csv(t.EntryReason), Csv(string.Join(" / ", t.Exits.Select(e => e.Reason).Distinct())),
                N(c?.ChangePct), N(c?.VwapDistPct), N(c?.RangePosition), N(c?.Strength), N(c?.AtrPct), N(c?.VolumeRatio), N(c?.SpreadTicks), N(c?.MinutesFromOpen),
                N(t.Settings.StopLossPct), N(t.Settings.TakeProfitPct), N(t.Settings.TrailingActivationPct), N(t.Settings.TrailingDistancePct), t.Settings.HoldOvernight));
        }
        return sb.ToString();
    }

    public static string SignalsCsv(AnalysisDataSet ds)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', "신호ID", "종목코드", "종목명", "전략", "운용", "시각", "가격", "결과", "상세", "후속결정", "신호근거",
            "등락률%", "VWAP이격%", "범위위치", "체결강도", "거래량배수", "스프레드틱", "5분후", "15분후", "30분후", "60분후", "60분최고", "60분최저"));
        foreach (var s in ds.Signals)
        {
            var d = ds.Decisions.GetValueOrDefault(s.SignalId);
            var f = ds.FollowUps.GetValueOrDefault(s.SignalId);
            var c = s.Context;
            sb.AppendLine(string.Join(',', Csv(s.SignalId), s.Symbol, Csv(s.Name), Csv(s.Strategy), s.BotMode, T(s.Time), N(s.Price),
                Csv(s.Decision), Csv(s.DecisionDetail ?? ""), Csv(d?.Decision ?? ""), Csv(s.Reason),
                N(c.ChangePct), N(c.VwapDistPct), N(c.RangePosition), N(c.Strength), N(c.VolumeRatio), N(c.SpreadTicks),
                N(f?.Price5m), N(f?.Price15m), N(f?.Price30m), N(f?.Price60m), N(f?.Max60m), N(f?.Min60m)));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 도우미

    private void Suggest(string text)
    {
        if (!_suggestions.Contains(text)) _suggestions.Add(text);
    }

    public static string ExitName(ExitKind k) => k switch
    {
        ExitKind.StopLoss => "손절",
        ExitKind.BreakEven => "본절",
        ExitKind.Trailing => "트레일링",
        ExitKind.TakeProfit => "목표 익절",
        ExitKind.PartialTakeProfit => "분할 익절",
        ExitKind.TimeStop => "타임스탑",
        ExitKind.ForceClose => "장마감 청산",
        ExitKind.NextDayOpen => "익일 시초",
        ExitKind.NextDayDeadline => "익일 청산시각",
        ExitKind.Manual => "수동",
        ExitKind.KillSwitch => "킬스위치",
        ExitKind.RiskLimit => "리스크 한도",
        _ => "기타",
    };

    public static string TimeBucket(DateTimeOffset t)
    {
        var tod = Kst.TimeOf(t);
        var start = new TimeOnly(tod.Hour, tod.Minute < 30 ? 0 : 30);
        return $"{start:HH\\:mm}~{start.AddMinutes(30):HH\\:mm}";
    }

    public static string Bucket(decimal? value, decimal[] edges, string unit)
    {
        if (value is not { } v) return "(없음)";
        for (var i = 0; i < edges.Length; i++)
        {
            if (v < edges[i])
                return i == 0 ? $"a. {edges[0].ToString(Inv)}{unit} 미만" : $"{(char)('a' + i)}. {edges[i - 1].ToString(Inv)}~{edges[i].ToString(Inv)}{unit}";
        }
        return $"{(char)('a' + edges.Length)}. {edges[^1].ToString(Inv)}{unit} 이상";
    }

    private static string Signed(decimal v) => v.ToString("+0.00;-0.00;0.00", Inv);
    private static string Fmt(decimal? v) => v is { } x ? x.ToString("0.00", Inv) : "-";
    private static string T(DateTimeOffset t) => Kst.ToKst(t).ToString("yyyy-MM-dd HH:mm:ss", Inv);
    private static string N(decimal? v) => v?.ToString(Inv) ?? "";
    private static string N(double v) => v.ToString(Inv);
    private static string N(int? v) => v?.ToString(Inv) ?? "";
    private static string Csv(string s) => s.Contains(',') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
