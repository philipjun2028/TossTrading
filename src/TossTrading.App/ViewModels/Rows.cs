using CommunityToolkit.Mvvm.ComponentModel;
using TossTrading.Domain;
using TossTrading.Engine;

namespace TossTrading.App.ViewModels;

/// <summary>스캐너 후보 행. 선택 유지를 위해 Symbol 기준으로 제자리 갱신한다.</summary>
public sealed partial class CandidateRow : ObservableObject
{
    public CandidateRow(string symbol) => Symbol = symbol;

    public string Symbol { get; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private decimal _price;
    [ObservableProperty] private decimal _changePct;
    [ObservableProperty] private decimal _tradingAmountEok;
    [ObservableProperty] private string _rvol = "";
    [ObservableProperty] private string _strength = "";
    [ObservableProperty] private decimal _tickCostPct;
    [ObservableProperty] private string _spread = "";
    [ObservableProperty] private string _vwapDist = "";
    [ObservableProperty] private decimal _score;
    [ObservableProperty] private string _tags = "";

    public void Update(ScanCandidate c)
    {
        Name = c.Name;
        Price = c.Price;
        ChangePct = Math.Round(c.ChangePct, 2);
        TradingAmountEok = Math.Round(c.TradingAmount / 100_000_000m, 0);
        Rvol = c.Rvol is { } r ? r.ToString("F1") : "-";
        Strength = c.Strength is { } s ? s.ToString("F0") : "-";
        TickCostPct = Math.Round(c.TickCostPct, 3);
        Spread = c.SpreadTicks?.ToString() ?? "-";
        VwapDist = c.VwapDistPct is { } v ? v.ToString("+0.0;-0.0") + "%" : "-";
        Score = c.Score;
        Tags = c.Tags;
    }
}

public sealed partial class BotRow : ObservableObject
{
    public BotRow(string id) => Id = id;

    public string Id { get; }
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _strategy = "";
    [ObservableProperty] private string _mode = "";
    [ObservableProperty] private BotState _state;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private decimal _quantity;
    [ObservableProperty] private decimal _averagePrice;
    [ObservableProperty] private decimal _lastPrice;
    [ObservableProperty] private string _stopPrice = "";
    [ObservableProperty] private decimal _unrealizedNet;
    [ObservableProperty] private decimal _unrealizedPct;
    [ObservableProperty] private decimal _realizedNet;
    [ObservableProperty] private decimal _targetProgress;
    [ObservableProperty] private string _targetText = "";
    [ObservableProperty] private string _entries = "";
    [ObservableProperty] private string? _pendingSignal;
    [ObservableProperty] private bool _hasPendingSignal;

    public BotSettings Settings { get; private set; } = new();

    public void Update(BotView v)
    {
        Symbol = v.Symbol;
        Name = v.Name;
        Strategy = v.Strategy;
        Mode = v.Mode switch { BotMode.ManualEntry => "수동진입", BotMode.SemiAuto => "반자동", _ => "완전자동" };
        State = v.State;
        StateText = v.StateText;
        Quantity = v.Quantity;
        AveragePrice = Math.Round(v.AveragePrice, 0);
        LastPrice = v.LastPrice;
        StopPrice = v.StopPrice is { } s ? s.ToString("N0") : "-";
        UnrealizedNet = Math.Round(v.UnrealizedNet, 0);
        UnrealizedPct = Math.Round(v.UnrealizedPct, 2);
        RealizedNet = Math.Round(v.RealizedNet, 0);
        TargetProgress = v.TargetProgress;
        TargetText = v.TargetAmount > 0 ? $"{v.RealizedNet:N0} / {v.TargetAmount:N0}" : "-";
        Entries = $"{v.Entries}/{v.MaxEntries} ({v.Wins}승 {v.Losses}패)";
        PendingSignal = v.PendingSignal;
        HasPendingSignal = v.PendingSignal is not null;
        Settings = v.Settings;
    }
}

public sealed record TradeRow(
    string Time, string Symbol, string Name, string Strategy, decimal Quantity,
    decimal Entry, decimal Exit, decimal NetPnl, decimal NetPct, string R, string ExitReason)
{
    public static TradeRow From(ClosedTrade t) => new(
        $"{Kst.TimeOf(t.EntryTime):HH\\:mm}~{Kst.TimeOf(t.ExitTime):HH\\:mm}", t.Symbol, t.Name, t.Strategy, t.Quantity,
        Math.Round(t.AverageEntry, 0), Math.Round(t.AverageExit, 0), Math.Round(t.NetPnl, 0), Math.Round(t.NetPct, 2),
        t.RMultiple is { } r ? r.ToString("+0.00;-0.00") + "R" : "-", t.ExitReason);
}

public sealed record LogRow(long Seq, string Time, string Level, string Source, string Message);

public sealed record EnumOption<T>(T Value, string Label) where T : struct, Enum;
