using System.Globalization;
using System.Text;
using TossTrading.Domain;
using TossTrading.Engine.Analytics;

namespace TossTrading.Engine.Backtest;

/// <summary>백테스트 결과 리포트 (마크다운) + CSV 저장</summary>
public static class BacktestReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Markdown(BacktestResult r)
    {
        var sb = new StringBuilder();
        var trades = r.Trades;
        var wins = trades.Where(t => t.NetPnl > 0).ToList();
        var losses = trades.Where(t => t.NetPnl <= 0).ToList();
        var grossWin = wins.Sum(t => t.NetPnl);
        var grossLoss = -losses.Sum(t => t.NetPnl);
        var tradingDays = r.Days.Count(d => d.Symbols > 0);
        var upDays = r.Days.Count(d => d.DayReturnPct > 0);

        sb.AppendLine($"# 백테스트 결과 ({r.From:yyyy-MM-dd} ~ {r.To:yyyy-MM-dd})");
        sb.AppendLine($"데이터: {r.DataName} · 거래일 {r.Days.Count}일 (재생 {tradingDays}일) · 소요 {r.Elapsed.TotalSeconds:F0}초{(r.Canceled ? " · **중간 취소됨**" : "")}");
        sb.AppendLine($"수익은 모두 수수료·세금 차감 후(순). 체결은 1분봉을 시가→고/저→저/고→종가 경로(구간당 {r.TicksPerLeg}체결, " +
                      $"{(r.IntrabarPath == IntrabarPath.NearestFirst ? "시가에서 가까운 극값 먼저" : "보수적: 양봉은 저가 먼저")})로 재생한 모의 체결입니다.");
        sb.AppendLine();
        sb.AppendLine("## 요약");
        sb.AppendLine("| 항목 | 값 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 시작 자금 | {r.StartingCash:N0}원 |");
        sb.AppendLine($"| 최종 평가금액 | {r.EndingEquity:N0}원 |");
        sb.AppendLine($"| **수익금** | **{r.NetProfit:+#,0;-#,0;0}원** |");
        sb.AppendLine($"| **수익률** | **{Signed(r.ReturnPct)}%** |");
        sb.AppendLine($"| 실현 손익 | {r.RealizedNet:+#,0;-#,0;0}원 (미실현 {r.NetProfit - r.RealizedNet:+#,0;-#,0;0}원) |");
        sb.AppendLine($"| 최대 낙폭 (MDD) | -{r.MaxDrawdownPct:F2}% |");
        sb.AppendLine($"| 거래 수 | {trades.Count}건 (단타·종가 포함) |");
        sb.AppendLine($"| 승률 | {(trades.Count > 0 ? wins.Count * 100m / trades.Count : 0):F1}% ({wins.Count}승 {losses.Count}패) |");
        sb.AppendLine($"| 평균 수익 / 평균 손실 | {(wins.Count > 0 ? Signed(wins.Average(t => t.NetPct)) : "-")}% / {(losses.Count > 0 ? Signed(losses.Average(t => t.NetPct)) : "-")}% |");
        sb.AppendLine($"| Profit Factor | {(grossLoss > 0 ? (grossWin / grossLoss).ToString("0.00", Inv) : "-")} |");
        sb.AppendLine($"| 수익 난 날 | {upDays}/{r.Days.Count}일 |");
        if (r.Days.Count > 0)
        {
            var best = r.Days.MaxBy(d => d.DayReturnPct)!;
            var worst = r.Days.MinBy(d => d.DayReturnPct)!;
            sb.AppendLine($"| 최고의 날 / 최악의 날 | {best.Date:MM/dd} {Signed(best.DayReturnPct)}% / {worst.Date:MM/dd} {Signed(worst.DayReturnPct)}% |");
        }
        sb.AppendLine();

        if (trades.Count > 0)
        {
            sb.AppendLine("## 전략별");
            sb.AppendLine("| 전략 | 거래 | 승률 | 평균 수익률 | 순손익(원) |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var g in trades.GroupBy(t => t.Strategy).OrderByDescending(g => g.Sum(t => t.NetPnl)))
                sb.AppendLine($"| {g.Key} | {g.Count()} | {g.Count(t => t.NetPnl > 0) * 100m / g.Count():F0}% | {Signed(g.Average(t => t.NetPct))}% | {g.Sum(t => t.NetPnl):+#,0;-#,0;0} |");
            sb.AppendLine();
        }

        sb.AppendLine("## 일별 손익");
        sb.AppendLine("| 날짜 | 종목 | 거래 | 승 | 실현손익(원) | 평가금액(원) | 일 수익률 | 누적 | 보유 | 비고 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var d in r.Days)
            sb.AppendLine($"| {d.Date:yyyy-MM-dd} | {d.Symbols} | {d.Trades} | {d.Wins} | {d.RealizedNet:+#,0;-#,0;0} | {d.EquityEnd:N0} | {Signed(d.DayReturnPct)}% | {Signed(d.CumulativeReturnPct)}% | {d.OpenPositions} | {d.Note} |");
        sb.AppendLine();

        sb.AppendLine("## 거래 내역");
        if (trades.Count == 0) sb.AppendLine("- 거래가 없습니다. (조건을 만족한 신호가 없었거나 데이터가 부족했습니다)");
        else
        {
            sb.AppendLine("| 매수 | 매도 | 종목 | 전략 | 수량 | 매수가 | 매도가 | 순손익(원) | 수익률 | 청산 사유 |");
            sb.AppendLine("|---|---|---|---|---:|---:|---:|---:|---:|---|");
            foreach (var t in trades)
                sb.AppendLine($"| {T(t.EntryTime)} | {T(t.ExitTime)} | {t.Name} | {t.Strategy} | {t.Quantity:N0} | {t.AverageEntry:N0} | {t.AverageExit:N0} | {t.NetPnl:+#,0;-#,0;0} | {Signed(t.NetPct)}% | {t.ExitReason} |");
        }
        sb.AppendLine();

        if (r.OpenPositions.Count > 0)
        {
            sb.AppendLine("## 기간 종료 시 보유 (미실현)");
            sb.AppendLine("| 종목 | 수량 | 평균가 | 현재가 | 평가손익(원) |");
            sb.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var p in r.OpenPositions)
                sb.AppendLine($"| {p.Name}({p.Symbol}) | {p.Quantity:N0} | {p.AveragePrice:N0} | {p.LastPrice:N0} | {p.UnrealizedNet:+#,0;-#,0;0} |");
            sb.AppendLine();
        }

        if (r.Warnings.Count > 0)
        {
            sb.AppendLine("## 주의");
            foreach (var w in r.Warnings.Take(30)) sb.AppendLine($"- {w}");
            if (r.Warnings.Count > 30) sb.AppendLine($"- … 외 {r.Warnings.Count - 30}건");
            sb.AppendLine();
        }

        sb.AppendLine("## 백테스트의 한계");
        sb.AppendLine("- 대상 종목은 코스피·코스닥 **현재 상장 종목 전체**입니다. 날짜별로 그날 후보가 될 수 있었던 종목만 분봉으로 재생하고, 각 시점의 순위는 분봉으로 다시 계산합니다. 기간 중 상장폐지된 종목은 빠집니다.");
        sb.AppendLine("- 1분봉 안의 실제 체결 순서·호가 잔량은 알 수 없어 합성합니다. 실제보다 좋거나 나쁠 수 있습니다.");
        sb.AppendLine("- 체결강도·VI·투자경고 등 실시간에만 있는 정보는 반영되지 않습니다.");
        sb.AppendLine("- 과거 성과가 미래 수익을 보장하지 않습니다. 설정을 바꿨다면 다른 기간으로도 확인하세요.");
        return sb.ToString();
    }

    public static string TradesCsv(IEnumerable<ClosedTrade> trades)
    {
        var sb = new StringBuilder();
        sb.AppendLine("매수시각,매도시각,종목코드,종목명,전략,수량,매수평균,매도평균,순손익,수익률%,R,진입근거,청산사유");
        foreach (var t in trades)
            sb.AppendLine(string.Join(',', T(t.EntryTime), T(t.ExitTime), t.Symbol, Csv(t.Name), Csv(t.Strategy), N(t.Quantity),
                N(Math.Round(t.AverageEntry, 2)), N(Math.Round(t.AverageExit, 2)), N(Math.Round(t.NetPnl, 0)), N(Math.Round(t.NetPct, 3)),
                t.RMultiple is { } r ? N(Math.Round(r, 2)) : "", Csv(t.EntryReason), Csv(t.ExitReason)));
        return sb.ToString();
    }

    public static string DailyCsv(IEnumerable<BacktestDay> days)
    {
        var sb = new StringBuilder();
        sb.AppendLine("날짜,재생종목,거래,승,실현손익,평가금액,일수익률%,누적수익률%,보유종목,비고");
        foreach (var d in days)
            sb.AppendLine(string.Join(',', d.Date.ToString("yyyy-MM-dd", Inv), d.Symbols, d.Trades, d.Wins, N(d.RealizedNet), N(d.EquityEnd),
                N(d.DayReturnPct), N(d.CumulativeReturnPct), d.OpenPositions, Csv(d.Note ?? "")));
        return sb.ToString();
    }

    /// <summary>
    /// OutputDirectory 에 report.md / trades.csv / daily.csv / options.json
    /// (+ 분석 리포트 analysis.md, 거래 상세 trades_detail.csv, 신호 signals.csv) 저장. 저장한 폴더를 돌려준다.
    /// </summary>
    public static string? Save(BacktestResult r, BacktestOptions? options = null)
    {
        if (r.OutputDirectory is null) return null;
        Directory.CreateDirectory(r.OutputDirectory);
        var bom = new UTF8Encoding(true); // 엑셀 한글
        File.WriteAllText(Path.Combine(r.OutputDirectory, "report.md"), Markdown(r), bom);
        File.WriteAllText(Path.Combine(r.OutputDirectory, "trades.csv"), TradesCsv(r.Trades), bom);
        File.WriteAllText(Path.Combine(r.OutputDirectory, "daily.csv"), DailyCsv(r.Days), bom);

        // 진입 조건·MFE/MAE 등 상세 분석 (실매매 [성과 분석]과 같은 리포트)
        var journal = Path.Combine(r.OutputDirectory, "journal");
        if (Directory.Exists(journal))
        {
            var filter = new ReportFilter(r.From, r.To.AddDays(7));
            var ds = AnalysisDataSet.Load(journal, filter);
            File.WriteAllText(Path.Combine(r.OutputDirectory, "analysis.md"), new PerformanceReport(ds, filter).BuildMarkdown(), bom);
            File.WriteAllText(Path.Combine(r.OutputDirectory, "trades_detail.csv"), PerformanceReport.TradesCsv(ds.Trades), bom);
            File.WriteAllText(Path.Combine(r.OutputDirectory, "signals.csv"), PerformanceReport.SignalsCsv(ds), bom);
            File.WriteAllText(Path.Combine(r.OutputDirectory, "execution.md"), ExecutionDiagnostics(ds, r), bom);
        }
        if (options is not null)
        {
            // 어떤 설정으로 돌린 결과인지 (나중에 설정을 바꿔 비교할 때)
            var json = System.Text.Json.JsonSerializer.Serialize(options, new System.Text.Json.JsonSerializerOptions(AnalyticsRecorder.Json) { WriteIndented = true });
            File.WriteAllText(Path.Combine(r.OutputDirectory, "options.json"), json, new UTF8Encoding(false));
        }
        return r.OutputDirectory;
    }

    /// <summary>체결 품질 진단: 청산 유형별 기준가 대비 체결가, 빠른 손절 비율, 재생 정밀도</summary>
    public static string ExecutionDiagnostics(AnalysisDataSet ds, BacktestResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 체결 품질 진단");
        sb.AppendLine($"재생 정밀도: 1분봉 구간당 {r.TicksPerLeg}개 체결 (1이면 봉 꼭짓점만 → 돌파는 봉 고가, 손절은 봉 저가에 체결되어 결과가 나빠짐)");
        sb.AppendLine($"봉 내부 순서: {(r.IntrabarPath == IntrabarPath.NearestFirst ? "시가에서 가까운 극값 먼저" : "보수적 (양봉은 저가 먼저, 음봉은 고가 먼저)")}");
        sb.AppendLine();
        sb.AppendLine("## 청산 유형별 체결가 (청산 기준가 대비 %)");
        sb.AppendLine("| 유형 | 건수 | 평균 | 중앙값 | 하위 10% | 최악 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var g in ds.Trades.SelectMany(t => t.Exits).Where(l => l.TriggerPrice > 0).GroupBy(l => l.Kind).OrderByDescending(g => g.Count()))
        {
            var v = g.Select(l => (l.Price / l.TriggerPrice - 1m) * 100m).OrderBy(x => x).ToList();
            sb.AppendLine($"| {PerformanceReport.ExitName(g.Key)} | {v.Count} | {v.Average():F3} | {v[v.Count / 2]:F3} | {v[v.Count / 10]:F3} | {v[0]:F2} |");
        }
        sb.AppendLine();
        sb.AppendLine("## 전략별 보유 시간 · 빠른 손절");
        sb.AppendLine("| 전략 | 거래 | 보유 중앙값(분) | 5분 안에 손절 | 기대값 |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var g in ds.Trades.GroupBy(t => t.Strategy))
        {
            var holds = g.Select(t => t.HoldMinutes).OrderBy(x => x).ToList();
            var fast = g.Count(t => t.FinalExitKind == ExitKind.StopLoss && t.HoldMinutes <= 5);
            sb.AppendLine($"| {g.Key} | {g.Count()} | {holds[holds.Count / 2]:F1} | {fast} ({fast * 100 / g.Count()}%) | {Signed(g.Average(t => t.NetPct))}% |");
        }
        sb.AppendLine();
        var gaps = ds.Trades.Where(t => t.Overnight && t.FinalExitKind == ExitKind.StopLoss).ToList();
        if (gaps.Count > 0)
            sb.AppendLine($"- 익일 보유 후 손절 {gaps.Count}건: 평균 {gaps.Average(t => t.NetPct):F2}%, 최악 {gaps.Min(t => t.NetPct):F2}% (갭하락은 실제로도 막을 수 없는 위험)");
        sb.AppendLine("- 거래별 전후 1분봉은 trade_bars.jsonl 에 있습니다.");
        return sb.ToString();
    }

    private static string Signed(decimal v) => v.ToString("+0.00;-0.00;0.00", Inv);
    private static string T(DateTimeOffset t) => Kst.ToKst(t).ToString("yyyy-MM-dd HH:mm", Inv);
    private static string N(decimal v) => v.ToString(Inv);
    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
