using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using DevExpress.Mvvm;
using DevExpress.Xpf.Core;
using TossTrading.App.Services;
using TossTrading.App.Views;
using TossTrading.Domain;
using TossTrading.Engine;

namespace TossTrading.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly EngineHost _host = new();
    private readonly DispatcherTimer _timer;
    private long _lastLogSeq;
    private int _lastTradeCount;
    private DateTime _lastChartUpdate;

    public MainViewModel()
    {
        Settings = SettingsStore.Load();
        DataSource = Settings.DataSource;
        Execution = Settings.Execution;
        Status = "중지됨 — [시작]을 눌러 주세요";
        ChartTitle = "차트: 종목을 선택하세요";
        AutoPilotEnabled = Settings.AutoPilot.Enabled;

        StartCommand = new AsyncCommand(StartAsync);
        StopCommand = new AsyncCommand(StopAsync);
        FlattenCommand = new AsyncCommand(() => BotCommand(id => Engine!.FlattenBotAsync(id)));
        StopBotCommand = new AsyncCommand(() => BotCommand(id => Engine!.StopBotAsync(id, flatten: true)));
        EditBotCommand = new AsyncCommand(EditBotAsync);
        KillSwitchCommand = new AsyncCommand(KillSwitchAsync);
        ResetKillSwitchCommand = new AsyncCommand(() => Engine is null ? Task.CompletedTask : Run(() => Engine.ResetKillSwitchAsync()));
        OpenSettingsCommand = new AsyncCommand(OpenSettingsAsync);
        OpenAnalysisCommand = new DelegateCommand(OpenAnalysis);
        OpenBacktestCommand = new DelegateCommand(OpenBacktest);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    public AppSettings Settings { get; private set; }

    public ObservableCollection<CandidateRow> Candidates { get; } = new();
    public ObservableCollection<BotRow> Bots { get; } = new();
    public ObservableCollection<TradeRow> Trades { get; } = new();
    public ObservableCollection<LogRow> Logs { get; } = new();

    public IReadOnlyList<EnumOption<DataSourceKind>> DataSourceOptions { get; } = new[]
    {
        new EnumOption<DataSourceKind>(DataSourceKind.Simulation, "시뮬레이션 (API 키 불필요)"),
        new EnumOption<DataSourceKind>(DataSourceKind.Toss, "토스 실시간"),
    };

    public IReadOnlyList<EnumOption<ExecutionMode>> ExecutionOptions { get; } = new[]
    {
        new EnumOption<ExecutionMode>(ExecutionMode.Paper, "모의 주문 (Paper)"),
        new EnumOption<ExecutionMode>(ExecutionMode.Live, "실전 주문 (Live)"),
    };

    /// <summary>스캐너 모드는 엔진이 시간대에 맞춰 바꾼다 (09:00~ 단타 후보, 14:40~ 종가매매 후보)</summary>
    public bool IsClosingMode => _engineScanMode == ScanMode.ClosingBet;

    private ScanMode? _engineScanMode;

    // ---------------------------------------------------------------- 자동 운용
    /// <summary>자동 운용 켜기/끄기. 엔진 실행 중이면 즉시 반영.</summary>
    public bool AutoPilotEnabled
    {
        get => GetValue<bool>();
        set => SetValue(value, () =>
        {
            Settings.AutoPilot.Enabled = value;
            SettingsStore.Save(Settings);
            if (Engine is not null) _ = Run(() => Engine.SetAutoPilotAsync(Settings.AutoPilotPlan()));
        });
    }

    public string AutoPilotStatus { get => GetValue<string>(); private set => SetValue(value); }

    private void RaiseScanModeChanged()
    {
        RaisePropertyChanged(nameof(IsClosingMode));
        RaisePropertyChanged(nameof(IsDayMode));
        RaisePropertyChanged(nameof(ScannerHeader));
    }
    public bool IsDayMode => !IsClosingMode;

    public string ScannerHeader => IsClosingMode
        ? "자동 선정 후보 — 종가매매 (조건 모두 통과 종목을 🤖 자동 운용이 선정)"
        : "자동 선정 후보 — 단타 (거래대금·RVOL·등락률 상위, 점수순으로 🤖 자동 운용이 선정)";

    // ---------------------------------------------------------------- 상태 속성
    public bool IsRunning
    {
        get => GetValue<bool>();
        private set => SetValue(value, () => RaisePropertyChanged(nameof(CanEditMode)));
    }
    public bool CanEditMode => !IsRunning;
    public bool IsBusy { get => GetValue<bool>(); private set => SetValue(value); }
    public DataSourceKind DataSource { get => GetValue<DataSourceKind>(); set => SetValue(value); }
    public ExecutionMode Execution { get => GetValue<ExecutionMode>(); set => SetValue(value); }
    public string Status { get => GetValue<string>(); private set => SetValue(value); }
    public string ModeBadge { get => GetValue<string>(); private set => SetValue(value); }
    public bool IsLive { get => GetValue<bool>(); private set => SetValue(value); }
    public string Clock { get => GetValue<string>(); private set => SetValue(value); }
    public bool FeedConnected { get => GetValue<bool>(); private set => SetValue(value); }

    public decimal StartEquity { get => GetValue<decimal>(); private set => SetValue(value); }
    public decimal Equity { get => GetValue<decimal>(); private set => SetValue(value); }
    public decimal Cash { get => GetValue<decimal>(); private set => SetValue(value); }
    public decimal RealizedNet { get => GetValue<decimal>(); private set => SetValue(value); }
    public decimal UnrealizedNet { get => GetValue<decimal>(); private set => SetValue(value); }
    public decimal DailyPnlPct { get => GetValue<decimal>(); private set => SetValue(value); }
    public double LossGauge { get => GetValue<double>(); private set => SetValue(value); }
    public string LossGaugeText { get => GetValue<string>(); private set => SetValue(value); }
    public string PositionsText { get => GetValue<string>(); private set => SetValue(value); }
    public string? BlockReason { get => GetValue<string?>(); private set => SetValue(value); }
    public bool KillSwitchActive { get => GetValue<bool>(); private set => SetValue(value); }

    public CandidateRow? SelectedCandidate
    {
        get => GetValue<CandidateRow?>();
        set => SetValue(value, () => { if (value is not null) _ = Engine?.SetFocusAsync(value.Symbol); });
    }

    public BotRow? SelectedBot
    {
        get => GetValue<BotRow?>();
        set => SetValue(value, () =>
        {
            RaisePropertyChanged(nameof(HasSelectedBot));
            if (value is not null) _ = Engine?.SetFocusAsync(value.Symbol);
        });
    }

    public bool HasSelectedBot => SelectedBot is not null;
    public ChartView? Chart { get => GetValue<ChartView?>(); private set => SetValue(value); }
    public string ChartTitle { get => GetValue<string>(); private set => SetValue(value); }

    // ---------------------------------------------------------------- 명령
    public AsyncCommand StartCommand { get; }
    public DelegateCommand OpenAnalysisCommand { get; }
    public DelegateCommand OpenBacktestCommand { get; }
    public AsyncCommand StopCommand { get; }
    public AsyncCommand FlattenCommand { get; }
    public AsyncCommand StopBotCommand { get; }
    public AsyncCommand EditBotCommand { get; }
    public AsyncCommand KillSwitchCommand { get; }
    public AsyncCommand ResetKillSwitchCommand { get; }
    public AsyncCommand OpenSettingsCommand { get; }

    private TradingEngine? Engine => _host.Engine;

    // ================================================================ 엔진 시작/중지

    private async Task StartAsync()
    {
        if (IsRunning || IsBusy) return;
        if (Execution == ExecutionMode.Live)
        {
            if (DataSource != DataSourceKind.Toss)
            {
                DXMessageBox.Show("실전 주문은 '토스 실시간' 데이터에서만 가능합니다.", "실전 모드", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var ok = DXMessageBox.Show(
                "실전(Live) 모드는 실제 계좌로 주문이 나갑니다.\n\n" +
                "• 페이퍼 트레이딩으로 충분히 검증했나요? (설계 문서 10장)\n" +
                "• 리스크 한도(일 손실 한도, 최대 투입금)를 확인했나요?\n" +
                "• 모든 손익의 책임은 사용자에게 있습니다.\n\n계속할까요?",
                "실전 주문 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (ok != MessageBoxResult.Yes) return;
        }

        IsBusy = true;
        Status = "시작 중...";
        try
        {
            Settings.DataSource = DataSource;
            Settings.Execution = Execution;
            SettingsStore.Save(Settings);
            await _host.StartAsync(Settings);
            IsRunning = true;
            _lastLogSeq = 0;
            _lastTradeCount = 0;
            Candidates.Clear();
            Bots.Clear();
            Trades.Clear();
            Logs.Clear();
        }
        catch (Exception ex)
        {
            Status = "시작 실패";
            DXMessageBox.Show(ex.Message, "엔진 시작 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopAsync()
    {
        if (!IsRunning) return;
        var s = Engine?.Snapshot;
        if (s is not null && s.Bots.Any(b => b.Quantity > 0))
        {
            var r = DXMessageBox.Show("보유 중인 봇이 있습니다. 엔진을 멈추면 청산 관리가 중단됩니다.\n그래도 중지할까요? (먼저 킬스위치/청산 권장)",
                "중지 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return;
        }
        IsBusy = true;
        try { await _host.StopAsync(); }
        finally
        {
            IsBusy = false;
            IsRunning = false;
            Status = "중지됨";
        }
    }

    public bool HasEngine => _host.Engine is not null;

    public void StopRefresh() => _timer.Stop();

    public async Task ShutdownAsync()
    {
        _timer.Stop();
        await _host.StopAsync();
    }

    // ================================================================ 봇 명령

    private async Task EditBotAsync()
    {
        if (SelectedBot is null || Engine is null) return;
        var vm = new BotSettingsViewModel(SelectedBot.Settings.Clone(), $"{SelectedBot.Name} 봇 설정 변경", Settings, isEdit: true,
            referencePrice: SelectedBot.LastPrice > 0 ? SelectedBot.LastPrice : 10_000m);
        var dlg = new BotSettingsWindow(vm) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        var id = SelectedBot.Id;
        await Run(() => Engine.UpdateBotSettingsAsync(id, vm.Result));
    }

    private async Task KillSwitchAsync()
    {
        if (Engine is null) return;
        var r = DXMessageBox.Show("킬스위치: 모든 봇을 정지하고, 미체결을 취소하고, 보유분을 시장가로 청산합니다.\n실행할까요?",
            "킬스위치", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r == MessageBoxResult.Yes) await Run(() => Engine.KillSwitchAsync());
    }

    /// <summary>성과 분석 창 (모달이 아니므로 매매 중에도 열어둘 수 있다)</summary>
    private void OpenAnalysis()
    {
        var existing = Application.Current.Windows.OfType<AnalysisWindow>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return; }
        new AnalysisWindow(new AnalysisViewModel()) { Owner = Application.Current.MainWindow }.Show();
    }

    /// <summary>백테스트 창 (모달 아님). 현재 설정의 복사본으로 실행한다.</summary>
    private void OpenBacktest()
    {
        var existing = Application.Current.Windows.OfType<BacktestWindow>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return; }
        var vm = new BacktestViewModel(SettingsStore.Clone(Settings), () => IsRunning && DataSource == DataSourceKind.Toss);
        new BacktestWindow(vm) { Owner = Application.Current.MainWindow }.Show();
    }

    private async Task OpenSettingsAsync()
    {
        var vm = new SettingsViewModel(SettingsStore.Clone(Settings), IsRunning);
        var dlg = new SettingsWindow(vm) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        Settings = vm.Settings;
        SettingsStore.Save(Settings);
        if (Engine is not null)
        {
            await Run(() => Engine.UpdateRiskAsync(Settings.Risk.Clone()));
            Engine.UpdateScanner(Settings.Scanner.Clone());
            await Run(() => Engine.SetAutoPilotAsync(Settings.AutoPilotPlan()));
        }
    }

    private Task BotCommand(Func<string, Task> action) =>
        SelectedBot is null || Engine is null ? Task.CompletedTask : Run(() => action(SelectedBot.Id));

    private static async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { DXMessageBox.Show(ex.Message, "알림", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    // ================================================================ 화면 갱신

    private void Refresh()
    {
        var engine = Engine;
        if (engine is null)
        {
            ModeBadge = $"{(DataSource == DataSourceKind.Simulation ? "SIM" : "TOSS")} · {(Execution == ExecutionMode.Live ? "LIVE" : "PAPER")}";
            IsLive = Execution == ExecutionMode.Live;
            return;
        }

        var s = engine.Snapshot;
        if (s.Time == DateTimeOffset.MinValue) return;

        Status = s.Status + (s.FeedConnected ? "" : " (시세 연결 대기/끊김)");
        FeedConnected = s.FeedConnected;
        ModeBadge = $"{(s.DataSource == DataSourceKind.Simulation ? "SIM" : "TOSS")} · {(s.Execution == ExecutionMode.Live ? "LIVE" : "PAPER")}";
        IsLive = s.Execution == ExecutionMode.Live;
        Clock = (s.DataSource == DataSourceKind.Simulation ? "가상 " : "") + Kst.TimeOf(s.Time).ToString("HH:mm:ss");

        StartEquity = s.Account.StartEquity;
        Equity = s.Account.Equity;
        Cash = s.Account.Cash;
        RealizedNet = Math.Round(s.Account.RealizedNet, 0);
        UnrealizedNet = Math.Round(s.Account.UnrealizedNet, 0);
        DailyPnlPct = Math.Round(s.Risk.DailyPnlPct, 2);
        LossGauge = s.Risk.DailyLossLimitPct > 0 ? (double)Math.Clamp(-s.Risk.DailyPnlPct / s.Risk.DailyLossLimitPct, 0, 1) * 100 : 0;
        LossGaugeText = $"{s.Risk.DailyPnlPct:+0.00;-0.00}% / 한도 -{s.Risk.DailyLossLimitPct}%";
        PositionsText = $"{s.Risk.OpenPositions} / {s.Risk.MaxPositions}";
        BlockReason = s.Risk.BlockReason;
        KillSwitchActive = s.Risk.KillSwitchActive;

        if (s.AutoPilot is { } ap)
        {
            AutoPilotStatus = ap.Enabled ? $"🤖 {ap.Phase} · {ap.Status}" : "🤖 꺼짐 · 새 종목 선정 중지 (보유 종목은 계속 관리)";
            if (_engineScanMode != ap.ScanMode)
            {
                _engineScanMode = ap.ScanMode;
                RaiseScanModeChanged();
            }
        }

        Sync(Candidates, s.Candidates, c => c.Symbol, c => new CandidateRow(c.Symbol), (row, c) => row.Update(c), r => r.Symbol, reorder: true);
        Sync(Bots, s.Bots, b => b.Id, b => new BotRow(b.Id), (row, b) => row.Update(b), r => r.Id, reorder: false);

        if (s.Trades.Count != _lastTradeCount)
        {
            foreach (var t in s.Trades.Skip(_lastTradeCount)) Trades.Insert(0, TradeRow.From(t));
            _lastTradeCount = s.Trades.Count;
        }

        foreach (var l in s.Logs.Where(l => l.Seq > _lastLogSeq))
        {
            Logs.Insert(0, new LogRow(l.Seq, Kst.TimeOf(l.Time).ToString("HH:mm:ss"), l.Level switch
            {
                LogLevel.Trade => "체결", LogLevel.Warn => "주의", LogLevel.Error => "오류", _ => "정보",
            }, l.Source, l.Message));
            _lastLogSeq = l.Seq;
        }
        while (Logs.Count > 500) Logs.RemoveAt(Logs.Count - 1);

        // 차트는 1초에 한 번만 다시 그린다 (DevExpress 차트 재바인딩 비용 절감)
        if (DateTime.UtcNow - _lastChartUpdate >= TimeSpan.FromSeconds(1) || (Chart?.Symbol != s.Chart?.Symbol))
        {
            _lastChartUpdate = DateTime.UtcNow;
            Chart = s.Chart;
            ChartTitle = s.Chart is { } ch ? $"차트: {ch.Name} ({ch.Symbol}) 1분봉 · VWAP" : "차트: 종목을 선택하세요";
        }
    }

    /// <summary>키 기준 제자리 갱신 (선택/스크롤 유지)</summary>
    private static void Sync<TRow, TData>(
        ObservableCollection<TRow> rows, IReadOnlyList<TData> data, Func<TData, string> dataKey,
        Func<TData, TRow> create, Action<TRow, TData> update, Func<TRow, string> rowKey, bool reorder)
    {
        var keys = data.Select(dataKey).ToHashSet();
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!keys.Contains(rowKey(rows[i]))) rows.RemoveAt(i);

        for (var i = 0; i < data.Count; i++)
        {
            var key = dataKey(data[i]);
            var idx = -1;
            for (var j = 0; j < rows.Count; j++) if (rowKey(rows[j]) == key) { idx = j; break; }
            TRow row;
            if (idx < 0)
            {
                row = create(data[i]);
                rows.Insert(Math.Min(i, rows.Count), row);
            }
            else
            {
                row = rows[idx];
                if (reorder && idx != i && i < rows.Count) rows.Move(idx, i);
            }
            update(row, data[i]);
        }
    }
}
