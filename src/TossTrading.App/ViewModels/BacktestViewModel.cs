using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using DevExpress.Mvvm;
using DevExpress.Xpf.Core;
using TossTrading.App.Services;
using TossTrading.Domain;
using TossTrading.Engine;
using TossTrading.Engine.Backtest;
using TossTrading.Toss;

namespace TossTrading.App.ViewModels;

/// <summary>
/// 백테스트 창: 기간·데이터·자금을 정해 과거 데이터로 자동 운용(단타→종가매매)을 재생하고
/// 거래 내역·일별 손익·수익률·수익금을 보여준다. 설정(봇 프리셋·리스크·스캐너·자동 운용 시각)은 현재 앱 설정을 그대로 쓴다.
/// </summary>
public sealed class BacktestViewModel : ViewModelBase
{
    public static string BacktestDirectory => Path.Combine(SettingsStore.DataDirectory, "backtests");
    public static string HistoryCacheDirectory => Path.Combine(SettingsStore.DataDirectory, "history");

    private readonly AppSettings _settings;
    private readonly Func<bool> _tossEngineRunning;
    private CancellationTokenSource? _cts;
    private BacktestResult? _result;

    public BacktestViewModel(AppSettings settings, Func<bool> tossEngineRunning)
    {
        _settings = settings;
        _tossEngineRunning = tossEngineRunning;
        var yesterday = DateTime.Today.AddDays(-1);
        ToDate = yesterday;
        FromDate = yesterday.AddMonths(-1);
        Source = string.IsNullOrEmpty(settings.TossClientId) ? "synthetic" : "toss";
        StartingCash = settings.PaperStartingCash > 0 ? settings.PaperStartingCash : 10_000_000m;
        DayTradingEnabled = settings.AutoPilot.DayTradingEnabled;
        ClosingEnabled = settings.AutoPilot.ClosingEnabled;
        MaxSymbolsPerDay = 60;
        IntrabarPath = "conservative";
        ExtraSymbols = "";
        Progress = 0;
        ProgressText = "기간과 데이터를 고르고 [▶ 실행]을 누르세요.";
        RunCommand = new AsyncCommand(RunAsync, () => !IsRunning);
        CancelCommand = new DelegateCommand(() => _cts?.Cancel(), () => IsRunning);
        OpenFolderCommand = new DelegateCommand(OpenFolder);
        ExportDataCommand = new AsyncCommand(ExportDataAsync, () => !IsRunning);
    }

    public IReadOnlyList<ChoiceOption<string>> SourceOptions { get; } = new[]
    {
        new ChoiceOption<string>("toss", "토스 과거 데이터 (API 키 필요)"),
        new ChoiceOption<string>("synthetic", "가상 데이터 (연습·동작 확인용)"),
    };

    // ---------------------------------------------------------------- 입력
    public DateTime FromDate { get => GetValue<DateTime>(); set => SetValue(value); }
    public DateTime ToDate { get => GetValue<DateTime>(); set => SetValue(value); }
    public string Source { get => GetValue<string>(); set => SetValue(value); }
    public decimal StartingCash { get => GetValue<decimal>(); set => SetValue(value); }
    public bool DayTradingEnabled { get => GetValue<bool>(); set => SetValue(value); }
    public bool ClosingEnabled { get => GetValue<bool>(); set => SetValue(value); }
    public int MaxSymbolsPerDay { get => GetValue<int>(); set => SetValue(value); }

    /// <summary>1분봉 내부 가격 순서 가정 (conservative / nearest)</summary>
    public string IntrabarPath { get => GetValue<string>(); set => SetValue(value); }

    public IReadOnlyList<ChoiceOption<string>> IntrabarPathOptions { get; } = new[]
    {
        new ChoiceOption<string>("conservative", "보수적 (양봉은 저가 먼저)"),
        new ChoiceOption<string>("nearest", "시가에서 가까운 쪽 먼저"),
    };
    public string ExtraSymbols { get => GetValue<string>(); set => SetValue(value); }

    // ---------------------------------------------------------------- 진행
    public bool IsRunning
    {
        get => GetValue<bool>();
        private set => SetValue(value, () =>
        {
            RunCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            ExportDataCommand?.RaiseCanExecuteChanged();
            RaisePropertyChanged(nameof(IsIdle));
        });
    }
    public bool IsIdle => !IsRunning;
    public double Progress { get => GetValue<double>(); private set => SetValue(value); }
    public string ProgressText { get => GetValue<string>(); private set => SetValue(value); }

    /// <summary>"45% · 경과 1분 20초 · 남은 시간 약 1분 40초"</summary>
    public string ProgressSummary { get => GetValue<string>(); private set => SetValue(value); }

    private readonly EtaEstimator _eta = new();
    private System.Windows.Threading.DispatcherTimer? _clock;

    private void UpdateSummary(double percent, TimeSpan? remaining)
    {
        var elapsed = _eta.Elapsed(DateTime.UtcNow);
        ProgressSummary = $"{percent:0}% · 경과 {EtaEstimator.Format(elapsed)}"
                          + (remaining is { } r ? $" · 남은 시간 약 {EtaEstimator.Format(r)}" : " · 남은 시간 계산 중");
    }

    // ---------------------------------------------------------------- 결과
    public bool HasResult { get => GetValue<bool>(); private set => SetValue(value); }
    public decimal ReturnPct { get => GetValue<decimal>(); private set => SetValue(value); }
    public decimal NetProfit { get => GetValue<decimal>(); private set => SetValue(value); }
    public decimal EndingEquity { get => GetValue<decimal>(); private set => SetValue(value); }
    public decimal MaxDrawdownPct { get => GetValue<decimal>(); private set => SetValue(value); }
    public string WinRateText { get => GetValue<string>(); private set => SetValue(value); }
    public string TradeCountText { get => GetValue<string>(); private set => SetValue(value); }
    public string ReportText { get => GetValue<string>(); private set => SetValue(value); }
    public ObservableCollection<BacktestDayRow> Days { get; } = new();
    public ObservableCollection<BacktestTradeRow> Trades { get; } = new();

    public AsyncCommand RunCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DelegateCommand OpenFolderCommand { get; }
    public AsyncCommand ExportDataCommand { get; }

    public static string ResearchExportDirectory => Path.Combine(SettingsStore.DataDirectory, "research_export");

    /// <summary>
    /// 백테스트 때 받아 둔 과거 데이터(분봉·일봉)를 연구용으로 압축해 내보낸다.
    /// 거래 로그만으로는 "다른 규칙이었다면?"을 시험할 수 없어서, 원본 데이터를 알고리즘 개선에 쓰기 위함.
    /// </summary>
    private async Task ExportDataAsync()
    {
        IsRunning = true;
        Progress = 0;
        _eta.Reset(DateTime.UtcNow);
        try
        {
            var progress = new Progress<BacktestProgress>(p =>
            {
                Progress = p.Overall;
                ProgressText = $"[내보내기] {p.Message}";
                UpdateSummary(p.Overall, _eta.Update(DateTime.UtcNow, p.Overall));
            });
            var s = await Task.Run(() => ResearchData.Export(HistoryCacheDirectory, ResearchExportDirectory, progress: progress));
            ProgressSummary = "내보내기 완료";
            ProgressText = $"종목 {s.Symbols:N0} · 분봉 {s.MinuteRows:N0}행 ({s.From:yyyy-MM-dd}~{s.To:yyyy-MM-dd}) · {s.Bytes / 1024.0 / 1024.0:F1}MB → {s.Directory}";
            try { Process.Start(new ProcessStartInfo { FileName = s.Directory, UseShellExecute = true }); } catch { }
        }
        catch (Exception ex)
        {
            ProgressText = $"내보내기 실패: {ex.Message}";
            DXMessageBox.Show(ex.Message, "연구용 데이터 내보내기");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task RunAsync()
    {
        var from = DateOnly.FromDateTime(FromDate);
        var to = DateOnly.FromDateTime(ToDate);
        if (to < from) { DXMessageBox.Show("종료일이 시작일보다 빠릅니다.", "백테스트"); return; }
        if (to.DayNumber - from.DayNumber > 370) { DXMessageBox.Show("기간은 1년 이내로 설정하세요.", "백테스트"); return; }
        if (!DayTradingEnabled && !ClosingEnabled) { DXMessageBox.Show("단타·종가매매 중 하나 이상을 선택하세요.", "백테스트"); return; }
        if (Source == "toss")
        {
            if (string.IsNullOrEmpty(_settings.TossClientId) || string.IsNullOrEmpty(_settings.TossClientSecret))
            {
                DXMessageBox.Show("토스 과거 데이터를 쓰려면 [설정 → 토스 연결]에 API 키를 입력하세요.", "백테스트");
                return;
            }
            if (_tossEngineRunning())
            {
                DXMessageBox.Show("토스 실시간 엔진이 실행 중입니다. API 호출 한도가 겹치지 않도록 엔진을 중지한 뒤 실행하세요.\n(가상 데이터 백테스트는 엔진 실행 중에도 가능합니다)", "백테스트");
                return;
            }
        }

        var auto = _settings.AutoPilot.Clone();
        auto.Enabled = true;
        auto.DayTradingEnabled = DayTradingEnabled;
        auto.ClosingEnabled = ClosingEnabled;
        var options = new BacktestOptions
        {
            From = from, To = to, StartingCash = StartingCash,
            Plan = AutoPilotPlan.FromPresets(auto, _settings.Presets),
            Risk = _settings.Risk.Clone(), Scanner = _settings.Scanner.Clone(), Cost = _settings.Cost.Clone(),
            MaxSymbolsPerDay = Math.Max(5, MaxSymbolsPerDay),
            IntrabarPath = IntrabarPath == "nearest" ? Engine.Backtest.IntrabarPath.NearestFirst : Engine.Backtest.IntrabarPath.Conservative,
            ExtraSymbols = ExtraSymbols.Split(new[] { ',', ' ', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries).ToList(),
            OutputDirectory = Path.Combine(BacktestDirectory, $"{DateTime.Now:yyyyMMdd_HHmmss}"),
        };

        IsRunning = true;
        HasResult = false;
        Progress = 0;
        _eta.Reset(DateTime.UtcNow);
        TimeSpan? lastEta = null;
        UpdateSummary(0, null);
        // 진행 이벤트가 뜸한 구간(네트워크 대기 등)에도 경과 시간은 계속 흐르게
        _clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateSummary(Progress, lastEta);
        _clock.Start();
        _cts = new CancellationTokenSource();
        TossConnection? conn = null;
        try
        {
            IHistoryProvider provider;
            if (Source == "toss")
            {
                conn = new TossConnection(EngineHost.ToTossOptions(_settings));
                provider = new CachedHistoryProvider(new TossHistoryProvider(conn.Source, from), HistoryCacheDirectory);
            }
            else
            {
                provider = new SyntheticHistoryProvider();
            }

            var progress = new Progress<BacktestProgress>(p =>
            {
                ProgressText = $"[{p.Stage}] {p.Message}";
                Progress = Math.Max(Progress, p.Overall); // 되돌아가지 않게
                lastEta = _eta.Update(DateTime.UtcNow, Progress);
                UpdateSummary(Progress, lastEta);
            });
            var ct = _cts.Token;
            var result = await Task.Run(() => new BacktestRunner(provider, options, progress).RunAsync(ct), ct);
            await Task.Run(() => BacktestReport.Save(result, options));
            Show(result);
            Progress = 100;
            UpdateSummary(100, TimeSpan.Zero);
            ProgressSummary = $"완료 · 소요 {EtaEstimator.Format(_eta.Elapsed(DateTime.UtcNow))}";
            ProgressText = (result.Canceled ? "취소됨 — 그때까지의 결과입니다. " : "완료. ") + $"저장: {result.OutputDirectory}";
        }
        catch (OperationCanceledException)
        {
            ProgressText = "취소됨";
        }
        catch (Exception ex)
        {
            ProgressText = $"실패: {ex.Message}";
            DXMessageBox.Show(ex.Message, "백테스트 실패");
        }
        finally
        {
            if (conn is not null) await conn.DisposeAsync();
            _clock?.Stop();
            _clock = null;
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    private void Show(BacktestResult r)
    {
        _result = r;
        ReturnPct = r.ReturnPct;
        NetProfit = r.NetProfit;
        EndingEquity = r.EndingEquity;
        MaxDrawdownPct = r.MaxDrawdownPct;
        var wins = r.Trades.Count(t => t.NetPnl > 0);
        WinRateText = r.Trades.Count > 0 ? $"{wins * 100.0 / r.Trades.Count:F1}% ({wins}승 {r.Trades.Count - wins}패)" : "-";
        TradeCountText = $"{r.Trades.Count}건 · {r.Days.Count}일";
        ReportText = BacktestReport.Markdown(r);
        Days.Clear();
        foreach (var d in r.Days) Days.Add(new BacktestDayRow(d));
        Trades.Clear();
        foreach (var t in r.Trades.OrderBy(t => t.EntryTime)) Trades.Add(new BacktestTradeRow(t));
        HasResult = true;
    }

    private void OpenFolder()
    {
        var path = _result?.OutputDirectory ?? BacktestDirectory;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { /* 무시 */ }
    }
}

public sealed class BacktestDayRow(BacktestDay d)
{
    public DateTime Date { get; } = d.Date.ToDateTime(TimeOnly.MinValue);
    public int Symbols { get; } = d.Symbols;
    public int Trades { get; } = d.Trades;
    public int Wins { get; } = d.Wins;
    public decimal RealizedNet { get; } = d.RealizedNet;
    public decimal EquityEnd { get; } = d.EquityEnd;
    public decimal DayReturnPct { get; } = d.DayReturnPct;
    public decimal CumulativeReturnPct { get; } = d.CumulativeReturnPct;
    public int OpenPositions { get; } = d.OpenPositions;
    public string Note { get; } = d.Note ?? "";
}

public sealed class BacktestTradeRow(ClosedTrade t)
{
    public DateTime EntryTime { get; } = Kst.ToKst(t.EntryTime).DateTime;
    public DateTime ExitTime { get; } = Kst.ToKst(t.ExitTime).DateTime;
    public string Name { get; } = $"{t.Name}({t.Symbol})";
    public string Strategy { get; } = t.Strategy;
    public decimal Quantity { get; } = t.Quantity;
    public decimal AverageEntry { get; } = Math.Round(t.AverageEntry, 0);
    public decimal AverageExit { get; } = Math.Round(t.AverageExit, 0);
    public decimal NetPnl { get; } = Math.Round(t.NetPnl, 0);
    public decimal NetPct { get; } = Math.Round(t.NetPct, 2);
    public string ExitReason { get; } = t.ExitReason;
    public string EntryReason { get; } = t.EntryReason;
}
