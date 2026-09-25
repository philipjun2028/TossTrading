using DevExpress.Mvvm;
using TossTrading.Domain;
using TossTrading.Engine;

namespace TossTrading.App.ViewModels;

/// <summary>스캐너 후보 행. 선택 유지를 위해 Symbol 기준으로 제자리 갱신한다.</summary>
public sealed class CandidateRow : BindableBase
{
    public CandidateRow(string symbol) => Symbol = symbol;

    public string Symbol { get; }
    public string Name { get => GetValue<string>(); set => SetValue(value); }
    public decimal Price { get => GetValue<decimal>(); set => SetValue(value); }
    public decimal ChangePct { get => GetValue<decimal>(); set => SetValue(value); }
    public decimal TradingAmountEok { get => GetValue<decimal>(); set => SetValue(value); }
    public decimal? Rvol { get => GetValue<decimal?>(); set => SetValue(value); }
    public decimal? Strength { get => GetValue<decimal?>(); set => SetValue(value); }
    public decimal TickCostPct { get => GetValue<decimal>(); set => SetValue(value); }
    public int? Spread { get => GetValue<int?>(); set => SetValue(value); }
    public decimal? VwapDist { get => GetValue<decimal?>(); set => SetValue(value); }
    public decimal Score { get => GetValue<decimal>(); set => SetValue(value); }
    public string Tags { get => GetValue<string>(); set => SetValue(value); }

    public void Update(ScanCandidate c)
    {
        Name = c.Name;
        Price = c.Price;
        ChangePct = Math.Round(c.ChangePct, 2);
        TradingAmountEok = Math.Round(c.TradingAmount / 100_000_000m, 0);
        Rvol = c.Rvol is { } r ? Math.Round(r, 1) : null;
        Strength = c.Strength is { } s ? Math.Round(s, 0) : null;
        TickCostPct = Math.Round(c.TickCostPct, 3);
        Spread = c.SpreadTicks;
        VwapDist = c.VwapDistPct is { } v ? Math.Round(v, 1) : null;
        Score = Math.Round(c.Score, 0);
        Tags = c.Tags;
    }
}

public sealed class BotRow : BindableBase
{
    public BotRow(string id) => Id = id;

    public string Id { get; }
    public string Symbol { get => GetValue<string>(); set => SetValue(value); }
    public string Name { get => GetValue<string>(); set => SetValue(value); }
    public string Strategy { get => GetValue<string>(); set => SetValue(value); }
    public string Mode { get => GetValue<string>(); set => SetValue(value); }
    public BotState State { get => GetValue<BotState>(); set => SetValue(value); }
    public string StateText { get => GetValue<string>(); set => SetValue(value); }
    public decimal Quantity { get => GetValue<decimal>(); set => SetValue(value); }
    public decimal AveragePrice { get => GetValue<decimal>(); set => SetValue(value); }
    public decimal LastPrice { get => GetValue<decimal>(); set => SetValue(value); }
    public decimal? StopPrice { get => GetValue<decimal?>(); set => SetValue(value); }
    public decimal UnrealizedNet { get => GetValue<decimal>(); set => SetValue(value); }
    public decimal UnrealizedPct { get => GetValue<decimal>(); set => SetValue(value); }
    public decimal RealizedNet { get => GetValue<decimal>(); set => SetValue(value); }
    public decimal TargetProgress { get => GetValue<decimal>(); set => SetValue(value); }
    public string TargetText { get => GetValue<string>(); set => SetValue(value); }
    public string Entries { get => GetValue<string>(); set => SetValue(value); }
    public string? PendingSignal { get => GetValue<string?>(); set => SetValue(value); }
    public bool HasPendingSignal { get => GetValue<bool>(); set => SetValue(value); }

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
        StopPrice = v.StopPrice;
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
    decimal Entry, decimal Exit, decimal NetPnl, decimal NetPct, decimal? R, string ExitReason)
{
    public static TradeRow From(ClosedTrade t) => new(
        $"{Kst.TimeOf(t.EntryTime):HH\\:mm}~{Kst.TimeOf(t.ExitTime):HH\\:mm}", t.Symbol, t.Name, t.Strategy, t.Quantity,
        Math.Round(t.AverageEntry, 0), Math.Round(t.AverageExit, 0), Math.Round(t.NetPnl, 0), Math.Round(t.NetPct, 2),
        t.RMultiple is { } r ? Math.Round(r, 2) : null, t.ExitReason);
}

public sealed record LogRow(long Seq, string Time, string Level, string Source, string Message);

public sealed record EnumOption<T>(T Value, string Label) where T : struct, Enum;
