using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TossTrading.App.Services;
using TossTrading.Domain;
using TossTrading.Engine.Strategies;
using TossTrading.Toss;

namespace TossTrading.App.ViewModels;

/// <summary>종목별 봇 설정 대화상자 (설계 문서 6.3)</summary>
public sealed partial class BotSettingsViewModel : ObservableObject
{
    public BotSettingsViewModel(BotSettings settings, string title, AppSettings app, bool isEdit = false, decimal referencePrice = 10_000m)
    {
        S = settings;
        Title = title;
        IsEdit = isEdit;
        _app = app;
        _referencePrice = referencePrice;
        Result = settings;
    }

    private readonly AppSettings _app;
    private readonly decimal _referencePrice;

    public event Action<bool>? RequestClose;

    public BotSettings S { get; }
    public string Title { get; }
    public bool IsEdit { get; }
    public bool ShowStartOption => !IsEdit;
    public BotSettings Result { get; private set; }

    [ObservableProperty] private bool _startImmediately = true;
    [ObservableProperty] private string _costHint = "";

    public IReadOnlyList<EnumOption<BotMode>> ModeOptions { get; } = new[]
    {
        new EnumOption<BotMode>(BotMode.ManualEntry, "A. 수동진입 · 자동청산"),
        new EnumOption<BotMode>(BotMode.SemiAuto, "B. 반자동 (신호 → 승인)"),
        new EnumOption<BotMode>(BotMode.FullAuto, "C. 완전자동"),
    };

    public IReadOnlyList<EnumOption<EntryStrategyKind>> StrategyOptions { get; } =
        Enum.GetValues<EntryStrategyKind>().Select(k => new EnumOption<EntryStrategyKind>(k, EntrySignalFactory.DisplayName(k) switch
        {
            "ORB" => "ORB (시가범위 돌파)",
            "VWAP눌림" => "VWAP 눌림 재돌파",
            "고가돌파" => "박스 상단(당일 고가) 돌파",
            var x => x,
        })).ToList();

    public IReadOnlyList<EnumOption<SizingMode>> SizingOptions { get; } = new[]
    {
        new EnumOption<SizingMode>(SizingMode.RiskBased, "리스크 기반 (권장)"),
        new EnumOption<SizingMode>(SizingMode.FixedAmount, "고정 금액"),
    };

    [RelayCommand]
    private void UpdateCostHint()
    {
        var cost = new CostModel(_app.Cost).RoundTripCostRate(_referencePrice) * 100m;
        var first = S.PartialTakeProfitPct > 0 ? S.PartialTakeProfitPct : S.TakeProfitPct;
        var guard = cost * 3;
        CostHint = first > 0 && first < guard
            ? $"⚠ 1차 목표 {first}% 가 왕복비용 {cost:F2}%의 3배({guard:F2}%)보다 작습니다. 비용에 수익이 잠식될 수 있습니다."
            : $"왕복비용 약 {cost:F2}% (가격 {_referencePrice:N0}원 기준) · 1차 목표 {first}%";
    }

    [RelayCommand]
    private void Ok()
    {
        var errors = S.Validate();
        if (errors.Count > 0)
        {
            MessageBox.Show(string.Join("\n", errors), "설정 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateCostHint();
        if (S.Mode == BotMode.FullAuto && _app.Execution == ExecutionMode.Live && CostHint.StartsWith('⚠'))
        {
            var r = MessageBox.Show(CostHint + "\n\n실전 완전자동으로 계속할까요?", "비용 가드", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }
        Result = S.Clone();
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}

/// <summary>앱 설정 대화상자: 토스 연결 / 리스크 / 스캐너 / 비용·기타</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(AppSettings settings, bool engineRunning)
    {
        Settings = settings;
        EngineRunning = engineRunning;
        _accountSeq = settings.TossAccountSeq;
    }

    public event Action<bool>? RequestClose;

    public AppSettings Settings { get; }
    public bool EngineRunning { get; }

    /// <summary>PasswordBox 에서 입력된 새 Secret (비어 있으면 기존 값 유지)</summary>
    public string NewClientSecret { get; set; } = "";

    public bool HasSavedSecret => !string.IsNullOrEmpty(Settings.TossClientSecretProtected);

    public ObservableCollection<string> Accounts { get; } = new();

    [ObservableProperty] private long _accountSeq;
    [ObservableProperty] private string _connectionResult = "";
    [ObservableProperty] private bool _isTesting;

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        ConnectionResult = "연결 확인 중...";
        try
        {
            var options = EngineHost.ToTossOptions(Settings);
            if (NewClientSecret.Length > 0) options.ClientSecret = NewClientSecret;
            using var rest = new TossRestClient(options);
            var accounts = await rest.GetAccountsAsync(CancellationToken.None);
            Accounts.Clear();
            foreach (var a in accounts) Accounts.Add($"{a.AccountSeq} · {a.AccountNo} · {a.AccountType}");
            if (AccountSeq == 0 && accounts.FirstOrDefault(a => a.AccountType == "BROKERAGE") is { } first) AccountSeq = first.AccountSeq;
            var price = await rest.GetPricesAsync(new[] { "005930" }, CancellationToken.None);
            ConnectionResult = $"✔ 연결 성공 — 계좌 {accounts.Count}개, 삼성전자 현재가 {price.FirstOrDefault()?.LastPrice:N0}원";
        }
        catch (Exception ex)
        {
            ConnectionResult = "✖ " + ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void ResetPresets()
    {
        if (MessageBox.Show("프리셋을 기본값으로 되돌릴까요?", "프리셋", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            Settings.Presets = BotPresets.CreateDefaults();
    }

    [RelayCommand]
    private void Save()
    {
        if (NewClientSecret.Length > 0) Settings.TossClientSecret = NewClientSecret;
        Settings.TossAccountSeq = AccountSeq;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}
