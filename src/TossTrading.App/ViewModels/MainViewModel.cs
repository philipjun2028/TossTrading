using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TossTrading.App.Services;
using TossTrading.App.Views;
using TossTrading.Domain;
using TossTrading.Engine;

namespace TossTrading.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly EngineHost _host = new();
    private readonly DispatcherTimer _timer;
    private long _lastLogSeq;
    private int _lastTradeCount;

    public MainViewModel()
    {
        Settings = SettingsStore.Load();
        _dataSource = Settings.DataSource;
        _execution = Settings.Execution;
        SelectedPreset = PresetNames.FirstOrDefault();

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

    public IReadOnlyList<string> PresetNames => Settings.Presets.Keys.ToList();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanEditMode))] private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private DataSourceKind _dataSource;
    [ObservableProperty] private ExecutionMode _execution;
    [ObservableProperty] private string _status = "중지됨 — [시작]을 눌러 주세요";
    [ObservableProperty] private string _modeBadge = "";
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private string _clock = "";
    [ObservableProperty] private bool _feedConnected;

    [ObservableProperty] private decimal _startEquity;
    [ObservableProperty] private decimal _equity;
    [ObservableProperty] private decimal _cash;
    [ObservableProperty] private decimal _realizedNet;
    [ObservableProperty] private decimal _unrealizedNet;
    [ObservableProperty] private decimal _dailyPnlPct;
    [ObservableProperty] private double _lossGauge;
    [ObservableProperty] private string _lossGaugeText = "";
    [ObservableProperty] private string _positionsText = "";
    [ObservableProperty] private string? _blockReason;
    [ObservableProperty] private bool _killSwitchActive;

    [ObservableProperty] private CandidateRow? _selectedCandidate;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelectedBot))] private BotRow? _selectedBot;
    [ObservableProperty] private string? _selectedPreset;
    [ObservableProperty] private string _manualSymbol = "";
    [ObservableProperty] private ChartView? _chart;
    [ObservableProperty] private string _chartTitle = "차트: 종목을 선택하세요";

    public bool CanEditMode => !IsRunning;
    public bool HasSelectedBot => SelectedBot is not null;

    private TradingEngine? Engine => _host.Engine;

    partial void OnSelectedCandidateChanged(CandidateRow? value)
    {
        if (value is not null) _ = Engine?.SetFocusAsync(value.Symbol);
    }

    partial void OnSelectedBotChanged(BotRow? value)
    {
        if (value is not null) _ = Engine?.SetFocusAsync(value.Symbol);
    }

    // ================================================================ 엔진 시작/중지

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning || IsBusy) return;
        if (Execution == ExecutionMode.Live)
        {
            if (DataSource != DataSourceKind.Toss)
            {
                MessageBox.Show("실전 주문은 '토스 실시간' 데이터에서만 가능합니다.", "실전 모드", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var ok = MessageBox.Show(
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
            MessageBox.Show(ex.Message, "엔진 시작 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsRunning) return;
        var s = Engine?.Snapshot;
        if (s is not null && s.Bots.Any(b => b.Quantity > 0))
        {
            var r = MessageBox.Show("보유 중인 봇이 있습니다. 엔진을 멈추면 청산 관리가 중단됩니다.\n그래도 중지할까요? (먼저 킬스위치/청산 권장)",
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

    public async Task ShutdownAsync()
    {
        _timer.Stop();
        await _host.StopAsync();
    }

    // ================================================================ 봇 명령

    [RelayCommand]
    private async Task AddSelectedCandidateAsync()
    {
        if (SelectedCandidate is null) { MessageBox.Show("스캐너에서 종목을 선택하세요."); return; }
        await AddBotAsync(SelectedCandidate.Symbol, SelectedCandidate.Name, SelectedCandidate.Price);
    }

    [RelayCommand]
    private async Task AddManualSymbolAsync()
    {
        var sym = ManualSymbol.Trim().ToUpperInvariant();
        if (sym.Length == 0) return;
        await AddBotAsync(sym, null, 10_000m);
        ManualSymbol = "";
    }

    private async Task AddBotAsync(string symbol, string? name, decimal referencePrice)
    {
        if (Engine is null) { MessageBox.Show("먼저 엔진을 시작하세요."); return; }
        var preset = SelectedPreset is not null && Settings.Presets.TryGetValue(SelectedPreset, out var p) ? p.Clone() : new BotSettings();
        var dlg = new BotSettingsWindow(new BotSettingsViewModel(preset, $"{name ?? symbol} ({symbol}) 봇 설정", Settings, referencePrice: referencePrice)) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        await Run(async () =>
        {
            var id = await Engine.AddBotAsync(symbol, name, dlg.ViewModel.Result);
            if (dlg.ViewModel.StartImmediately) await Engine.StartBotAsync(id);
        });
    }

    [RelayCommand] private Task StartBotAsync() => BotCommand(id => Engine!.StartBotAsync(id));
    [RelayCommand] private Task ManualBuyAsync() => BotCommand(id => Engine!.ManualBuyAsync(id));
    [RelayCommand] private Task ApproveAsync() => BotCommand(id => Engine!.ApproveSignalAsync(id));
    [RelayCommand] private Task RejectAsync() => BotCommand(id => Engine!.RejectSignalAsync(id));
    [RelayCommand] private Task FlattenAsync() => BotCommand(id => Engine!.FlattenBotAsync(id));
    [RelayCommand] private Task StopBotAsync() => BotCommand(id => Engine!.StopBotAsync(id, flatten: true));
    [RelayCommand] private Task RemoveBotAsync() => BotCommand(id => Engine!.RemoveBotAsync(id));

    [RelayCommand]
    private async Task EditBotAsync()
    {
        if (SelectedBot is null || Engine is null) return;
        var dlg = new BotSettingsWindow(new BotSettingsViewModel(SelectedBot.Settings.Clone(), $"{SelectedBot.Name} 봇 설정 변경", Settings, isEdit: true))
        { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        var id = SelectedBot.Id;
        await Run(() => Engine.UpdateBotSettingsAsync(id, dlg.ViewModel.Result));
    }

    [RelayCommand]
    private async Task KillSwitchAsync()
    {
        if (Engine is null) return;
        var r = MessageBox.Show("킬스위치: 모든 봇을 정지하고, 미체결을 취소하고, 보유분을 시장가로 청산합니다.\n실행할까요?",
            "킬스위치", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (r == MessageBoxResult.Yes) await Run(() => Engine.KillSwitchAsync());
    }

    [RelayCommand]
    private Task ResetKillSwitchAsync() => Engine is null ? Task.CompletedTask : Run(() => Engine.ResetKillSwitchAsync());

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var vm = new SettingsViewModel(SettingsStore.Clone(Settings), IsRunning);
        var dlg = new SettingsWindow(vm) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        Settings = vm.Settings;
        SettingsStore.Save(Settings);
        OnPropertyChanged(nameof(PresetNames));
        if (Engine is not null)
        {
            await Run(() => Engine.UpdateRiskAsync(Settings.Risk.Clone()));
            Engine.UpdateScanner(Settings.Scanner.Clone());
        }
    }

    [RelayCommand]
    private void SaveAsPreset()
    {
        if (SelectedBot is null) return;
        var name = $"{SelectedBot.Strategy} {DateTime.Now:MMdd-HHmm}";
        Settings.Presets[name] = SelectedBot.Settings.Clone();
        SettingsStore.Save(Settings);
        OnPropertyChanged(nameof(PresetNames));
        SelectedPreset = name;
    }

    private Task BotCommand(Func<string, Task> action) =>
        SelectedBot is null || Engine is null ? Task.CompletedTask : Run(() => action(SelectedBot.Id));

    private static async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "알림", MessageBoxButton.OK, MessageBoxImage.Information); }
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

        Chart = s.Chart;
        ChartTitle = s.Chart is { } ch ? $"차트: {ch.Name} ({ch.Symbol}) 1분봉 · VWAP" : "차트: 종목을 선택하세요";
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
