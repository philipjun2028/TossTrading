using System.Collections.ObjectModel;
using System.Windows;
using DevExpress.Mvvm;
using DevExpress.Xpf.Core;
using TossTrading.App.Services;
using TossTrading.Domain;
using TossTrading.Engine.Strategies;
using TossTrading.Toss;

namespace TossTrading.App.ViewModels;

/// <summary>종목별 봇 설정 대화상자 (설계 문서 6.3)</summary>
public sealed class BotSettingsViewModel : ViewModelBase
{
    private readonly AppSettings _app;
    private readonly decimal _referencePrice;

    public BotSettingsViewModel(BotSettings settings, string title, AppSettings app, bool isEdit = false, decimal referencePrice = 10_000m)
    {
        S = settings;
        Title = title;
        IsEdit = isEdit;
        _app = app;
        _referencePrice = referencePrice;
        Result = settings;
        StartImmediately = true;
        CostHint = "";
        UpdateCostHintCommand = new DelegateCommand(UpdateCostHint);
        OkCommand = new DelegateCommand(Ok);
        CancelCommand = new DelegateCommand(() => RequestClose?.Invoke(false));
    }

    public event Action<bool>? RequestClose;

    public BotSettings S { get; }
    public string Title { get; }
    public bool IsEdit { get; }
    public bool ShowStartOption => !IsEdit;
    public BotSettings Result { get; private set; }

    public bool StartImmediately { get => GetValue<bool>(); set => SetValue(value); }
    public string CostHint { get => GetValue<string>(); private set => SetValue(value); }

    public DelegateCommand UpdateCostHintCommand { get; }
    public DelegateCommand OkCommand { get; }
    public DelegateCommand CancelCommand { get; }

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
            "종가베팅" => "종가베팅 (장 마감 전 매수 → 익일 매도)",
            var x => x,
        })).ToList();

    public IReadOnlyList<EnumOption<NextDayExitMode>> NextDayExitOptions { get; } = new[]
    {
        new EnumOption<NextDayExitMode>(NextDayExitMode.Managed, "관리 (손절·익절·트레일링 후 청산 시각에 매도)"),
        new EnumOption<NextDayExitMode>(NextDayExitMode.AtOpen, "시초 매도 (장 시작 직후 전량 시장가)"),
    };

    public IReadOnlyList<EnumOption<SizingMode>> SizingOptions { get; } = new[]
    {
        new EnumOption<SizingMode>(SizingMode.RiskBased, "리스크 기반 (권장)"),
        new EnumOption<SizingMode>(SizingMode.FixedAmount, "고정 금액"),
    };

    public void UpdateCostHint()
    {
        var cost = new CostModel(_app.Cost).RoundTripCostRate(_referencePrice) * 100m;
        var first = S.PartialTakeProfitPct > 0 ? S.PartialTakeProfitPct : S.TakeProfitPct;
        var guard = cost * 3;
        CostHint = first > 0 && first < guard
            ? $"⚠ 1차 목표 {first}% 가 왕복비용 {cost:F2}%의 3배({guard:F2}%)보다 작습니다. 비용에 수익이 잠식될 수 있습니다."
            : $"왕복비용 약 {cost:F2}% (가격 {_referencePrice:N0}원 기준) · 1차 목표 {first}% (가격 기준, 순수익은 약 0.23%p 낮음)";
    }

    private void Ok()
    {
        var errors = S.Validate();
        if (errors.Count > 0)
        {
            DXMessageBox.Show(string.Join("\n", errors), "설정 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateCostHint();
        if (S.Mode == BotMode.FullAuto && _app.Execution == ExecutionMode.Live && CostHint.StartsWith('⚠'))
        {
            var r = DXMessageBox.Show(CostHint + "\n\n실전 완전자동으로 계속할까요?", "비용 가드", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }
        Result = S.Clone();
        RequestClose?.Invoke(true);
    }
}

/// <summary>앱 설정 대화상자: 토스 연결 / 리스크 / 스캐너 / 비용·기타</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    public SettingsViewModel(AppSettings settings, bool engineRunning)
    {
        Settings = settings;
        EngineRunning = engineRunning;
        AccountSeq = settings.TossAccountSeq;
        NewClientSecret = "";
        ConnectionResult = "";
        TestConnectionCommand = new AsyncCommand(TestConnectionAsync);
        ResetPresetsCommand = new DelegateCommand(ResetPresets);
        ResetPaperAccountCommand = new DelegateCommand(ResetPaperAccount, () => !EngineRunning);
        SaveCommand = new DelegateCommand(Save);
        CancelCommand = new DelegateCommand(() => RequestClose?.Invoke(false));
    }

    public event Action<bool>? RequestClose;

    public AppSettings Settings { get; }
    public bool EngineRunning { get; }
    public IReadOnlyList<string> PresetNames => Settings.Presets.Keys.ToList();
    public bool HasSavedSecret => !string.IsNullOrEmpty(Settings.TossClientSecretProtected);

    /// <summary>새로 입력한 Secret (비어 있으면 기존 저장값 유지)</summary>
    public string NewClientSecret { get => GetValue<string>(); set => SetValue(value); }

    public ObservableCollection<string> Accounts { get; } = new();

    public long AccountSeq { get => GetValue<long>(); set => SetValue(value); }
    public string ConnectionResult { get => GetValue<string>(); private set => SetValue(value); }

    public AsyncCommand TestConnectionCommand { get; }
    public DelegateCommand ResetPresetsCommand { get; }
    public DelegateCommand ResetPaperAccountCommand { get; }
    public DelegateCommand SaveCommand { get; }
    public DelegateCommand CancelCommand { get; }

    private async Task TestConnectionAsync()
    {
        ConnectionResult = "연결 확인 중...";
        try
        {
            var options = EngineHost.ToTossOptions(Settings);
            if (!string.IsNullOrEmpty(NewClientSecret)) options.ClientSecret = NewClientSecret;
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
    }

    private void ResetPresets()
    {
        if (DXMessageBox.Show("프리셋을 기본값으로 되돌릴까요?", "프리셋", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            Settings.Presets = BotPresets.CreateDefaults();
    }

    /// <summary>토스 실시간 + 모의 주문의 저장된 모의계좌·봇을 지운다 (엔진 정지 상태에서만)</summary>
    private void ResetPaperAccount()
    {
        if (DXMessageBox.Show("모의계좌(예수금·보유)와 저장된 모의 봇을 모두 지우고 새로 시작할까요?", "모의계좌 초기화",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        foreach (var f in new[] { "paper_account.json", "bots_paper.json" })
        {
            var path = System.IO.Path.Combine(EngineHost.StateDirectory, f);
            try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* 무시 */ }
        }
        ConnectionResult = "모의계좌를 초기화했습니다. 다음 시작 때 '모의 시작 예수금'으로 새로 시작합니다.";
    }

    private void Save()
    {
        var errors = Settings.AutoPilot.Validate();
        if (errors.Count > 0)
        {
            DXMessageBox.Show(string.Join("\n", errors), "설정 확인", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!string.IsNullOrEmpty(NewClientSecret)) Settings.TossClientSecret = NewClientSecret;
        Settings.TossAccountSeq = AccountSeq;
        RequestClose?.Invoke(true);
    }
}
