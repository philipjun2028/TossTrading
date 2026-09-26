using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using DevExpress.Mvvm;
using DevExpress.Xpf.Core;
using TossTrading.App.Services;
using TossTrading.Domain;
using TossTrading.Engine.Analytics;

namespace TossTrading.App.ViewModels;

/// <summary>성과 분석 창: journal/analysis_*.jsonl 을 읽어 리포트·개선 제안·거래 상세를 보여준다.</summary>
public sealed class AnalysisViewModel : ViewModelBase
{
    public static string JournalDirectory => Path.Combine(SettingsStore.DataDirectory, "journal");
    public static string ReportDirectory => Path.Combine(SettingsStore.DataDirectory, "reports");

    private AnalysisDataSet? _data;

    public AnalysisViewModel()
    {
        Days = 30;
        Source = "all";
        AnalyzeCommand = new AsyncCommand(AnalyzeAsync);
        ExportCommand = new DelegateCommand(Export, () => _data is not null);
        OpenFolderCommand = new DelegateCommand(() => OpenFolder(Directory.Exists(ReportDirectory) ? ReportDirectory : JournalDirectory));
    }

    public IReadOnlyList<ChoiceOption<int>> DayOptions { get; } = new[]
    {
        new ChoiceOption<int>(1, "오늘"),
        new ChoiceOption<int>(5, "최근 5일"),
        new ChoiceOption<int>(30, "최근 30일"),
        new ChoiceOption<int>(90, "최근 90일"),
        new ChoiceOption<int>(3650, "전체"),
    };

    public IReadOnlyList<ChoiceOption<string>> SourceOptions { get; } = new[]
    {
        new ChoiceOption<string>("all", "전체"),
        new ChoiceOption<string>("toss", "토스 실시간만"),
        new ChoiceOption<string>("sim", "시뮬레이션만"),
    };

    public int Days { get => GetValue<int>(); set => SetValue(value); }
    public string Source { get => GetValue<string>(); set => SetValue(value); }
    public string ReportText { get => GetValue<string>(); private set => SetValue(value); }
    public string Status { get => GetValue<string>(); private set => SetValue(value); }
    public ObservableCollection<AnalysisTradeRow> Trades { get; } = new();
    public ObservableCollection<string> Suggestions { get; } = new();

    public AsyncCommand AnalyzeCommand { get; }
    public DelegateCommand ExportCommand { get; }
    public DelegateCommand OpenFolderCommand { get; }

    private ReportFilter CurrentFilter() => ReportFilter.LastDays(Days, Kst.DateOf(Kst.Now)) with
    {
        DataSource = Source switch { "toss" => DataSourceKind.Toss, "sim" => DataSourceKind.Simulation, _ => null },
    };

    private async Task AnalyzeAsync()
    {
        Status = "분석 중...";
        var filter = CurrentFilter();
        try
        {
            var (data, text, suggestions) = await Task.Run(() =>
            {
                var ds = AnalysisDataSet.Load(JournalDirectory, filter);
                var report = new PerformanceReport(ds, filter);
                var md = report.BuildMarkdown();
                return (ds, md, report.Suggestions.ToList());
            });
            _data = data;
            ReportText = text;
            Suggestions.Clear();
            foreach (var s in suggestions) Suggestions.Add(s);
            Trades.Clear();
            foreach (var t in data.Trades.OrderByDescending(t => t.ExitTime)) Trades.Add(new AnalysisTradeRow(t));
            Status = $"거래 {data.Trades.Count}건 · 신호 {data.Signals.Count}건 · 제안 {suggestions.Count}개" +
                     (data.BadLines > 0 ? $" · 읽지 못한 줄 {data.BadLines}" : "");
            ExportCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            Status = $"분석 실패: {ex.Message}";
        }
    }

    private void Export()
    {
        if (_data is null) return;
        try
        {
            Directory.CreateDirectory(ReportDirectory);
            var stamp = $"{Kst.Now:yyyyMMdd_HHmmss}";
            var bom = new UTF8Encoding(true); // 엑셀 한글 깨짐 방지
            File.WriteAllText(Path.Combine(ReportDirectory, $"report_{stamp}.md"), ReportText ?? "", bom);
            File.WriteAllText(Path.Combine(ReportDirectory, $"trades_{stamp}.csv"), PerformanceReport.TradesCsv(_data.Trades), bom);
            File.WriteAllText(Path.Combine(ReportDirectory, $"signals_{stamp}.csv"), PerformanceReport.SignalsCsv(_data), bom);
            Status = $"저장 완료: {ReportDirectory}";
            OpenFolder(ReportDirectory);
        }
        catch (Exception ex)
        {
            DXMessageBox.Show($"저장 실패: {ex.Message}", "성과 분석");
        }
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { /* 탐색기 실행 실패는 무시 */ }
    }
}

public sealed record ChoiceOption<T>(T Value, string Label);

/// <summary>거래 상세 그리드 행</summary>
public sealed class AnalysisTradeRow
{
    public AnalysisTradeRow(TradeAnalysisRecord t)
    {
        ExitTime = Kst.ToKst(t.ExitTime).DateTime;
        Name = $"{t.Name}({t.Symbol})";
        Strategy = t.Strategy;
        Source = t.DataSource switch
        {
            DataSourceKind.Toss => t.Execution == ExecutionMode.Live ? "실전" : "토스·모의",
            DataSourceKind.Backtest => "백테스트",
            _ => "시뮬",
        };
        EntryPrice = t.AverageEntry;
        ExitPrice = t.AverageExit;
        Quantity = t.Quantity;
        NetPnl = t.NetPnl;
        NetPct = t.NetPct;
        R = t.RMultiple;
        MfePct = t.MfePct;
        MaePct = t.MaePct;
        RiskPct = t.RiskPct;
        HoldMinutes = t.HoldMinutes;
        ExitKind = PerformanceReport.ExitName(t.FinalExitKind);
        EntryReason = t.EntryReason;
        ChangePct = t.EntryContext?.ChangePct;
        VwapDistPct = t.EntryContext?.VwapDistPct;
        RangePosPct = t.EntryContext?.RangePosition is { } rp ? Math.Round(rp * 100m, 0) : null;
        Strength = t.EntryContext?.Strength;
        VolumeRatio = t.EntryContext?.VolumeRatio;
        EntrySlippagePct = t.EntrySlippagePct;
    }

    public DateTime ExitTime { get; }
    public string Name { get; }
    public string Strategy { get; }
    public string Source { get; }
    public decimal EntryPrice { get; }
    public decimal ExitPrice { get; }
    public decimal Quantity { get; }
    public decimal NetPnl { get; }
    public decimal NetPct { get; }
    public decimal? R { get; }
    public decimal MfePct { get; }
    public decimal MaePct { get; }
    public decimal RiskPct { get; }
    public double HoldMinutes { get; }
    public string ExitKind { get; }
    public string EntryReason { get; }
    public decimal? ChangePct { get; }
    public decimal? VwapDistPct { get; }
    public decimal? RangePosPct { get; }
    public decimal? Strength { get; }
    public decimal? VolumeRatio { get; }
    public decimal EntrySlippagePct { get; }
}
