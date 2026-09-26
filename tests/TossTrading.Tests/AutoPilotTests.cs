using TossTrading.Domain;
using TossTrading.Engine;
using TossTrading.Engine.Automation;
using TossTrading.Engine.Market;
using TossTrading.Engine.Trading;

namespace TossTrading.Tests;

internal sealed class FakeAutoPilotHost : IAutoPilotHost
{
    public readonly FakeHost BotHost = new();
    public readonly List<TradingBot> BotList = new();
    public readonly List<string> Logs = new();
    public List<ScanCandidate> CandidateList { get; set; } = new();
    public ScanMode? Mode { get; private set; }
    public string? Block { get; set; }
    public ManualClock Clock => BotHost.Clock;

    public DateTimeOffset Now => Clock.Now;
    public IReadOnlyList<ScanCandidate> Candidates => CandidateList;
    public IReadOnlyList<TradingBot> Bots => BotList;
    public string? EntryBlockReason => Block;
    public void SetScanMode(ScanMode? mode) => Mode = mode;

    public TradingBot? AddAutoBot(string symbol, string name, BotSettings settings, string role)
    {
        var ctx = new SymbolContext(symbol, name);
        ctx.OnOrderBook(new OrderBookSnapshot(symbol, Now, new[] { new PriceLevel(10_010m, 100) }, new[] { new PriceLevel(10_000m, 100) }));
        ctx.OnTrade(new TradeTick(symbol, 10_000m, 10, Now));
        var bot = new TradingBot($"{symbol}#{BotList.Count + 1}", ctx, settings, BotHost) { AutoRole = role };
        bot.Start();
        BotList.Add(bot);
        return bot;
    }

    public void RemoveBot(TradingBot bot) => BotList.Remove(bot);
    public void Log(LogLevel level, string message) => Logs.Add(message);
}

public class AutoPilotTests
{
    private static readonly DateOnly Date = new(2026, 9, 25);
    private readonly FakeAutoPilotHost _host = new();
    private readonly AutoPilot _ap;

    public AutoPilotTests() => _ap = new AutoPilot(_host, AutoPilotPlan.Default(enabled: true));

    private void At(int h, int m, int s = 0) => _host.Clock.Now = Kst.At(Date, new TimeOnly(h, m, s));

    private void Step(int seconds = 3)
    {
        _host.Clock.Now = _host.Clock.Now.AddSeconds(seconds);
        _ap.OnTimer();
    }

    private static ScanCandidate Day(string sym, decimal score) =>
        new(sym, "종목" + sym, 10_000m, 8m, 10_000_000_000m, 3m, 120m, 0.1m, 1, 1m, 0.8m, score, "");

    private static ScanCandidate Closing(string sym, int passed, int total = 6, decimal score = 70) =>
        Day(sym, score) with { ClosingPassed = passed, ClosingTotal = total, ClosingChecks = "" };

    [Fact]
    public void DayPhasePicksTopCandidatesAsFullAutoBots()
    {
        At(9, 10);
        _host.CandidateList = new() { Day("A", 80), Day("B", 70), Day("C", 40), Day("D", 65), Day("E", 60) };
        _ap.OnTimer();

        Assert.Equal(ScanMode.DayTrading, _host.Mode);
        Assert.Equal(new[] { "A", "B", "D" }, _host.BotList.Select(b => b.Symbol));   // 최대 3개, 점수 50 미만 제외
        var bot = _host.BotList[0];
        Assert.Equal(AutoPilot.DayRole, bot.AutoRole);
        Assert.Equal(BotMode.FullAuto, bot.Settings.Mode);
        Assert.Equal(EntryStrategyKind.OpeningRangeBreakout, bot.Settings.Strategy);   // 10시 전 → 오전 프리셋
        Assert.Equal(new TimeOnly(11, 0), bot.Settings.EntryEndTime);             // 프리셋의 진입 시간대를 넓히지 않음
        Assert.Equal(new TimeOnly(14, 50), bot.Settings.ForceExitTime);
        Assert.Equal(1, bot.Settings.MaxEntries);                                   // ORB 는 하루 첫 돌파 1회
        Assert.Equal(BotState.Watching, bot.State);
    }

    [Fact]
    public void NoDayPresetMeansNoNewBotsAfterMorning()
    {
        At(10, 30);
        _host.CandidateList = new() { Day("A", 80) };
        _ap.OnTimer();                                                              // 기본: 장중 프리셋 "사용 안 함"
        Assert.Empty(_host.BotList);
    }

    [Fact]
    public void AfternoonUsesDayPresetAndDoesNotReuseSymbols()
    {
        _ap.UpdatePlan(AutoPilotPlan.FromPresets(new AutoPilotSettings { Enabled = true, DayPreset = "VWAP 눌림 표준" }, BotPresets.CreateDefaults()));
        At(11, 0);
        _host.CandidateList = new() { Day("A", 80) };
        _ap.OnTimer();
        var bot = Assert.Single(_host.BotList);
        Assert.Equal(EntryStrategyKind.VwapReclaim, bot.Settings.Strategy);

        bot.Stop(flatten: false);                                                   // 봇이 끝나면 정리되고
        Step();
        Assert.Empty(_host.BotList);
        Step();
        Assert.Empty(_host.BotList);                                                // 같은 날 같은 종목은 다시 안 고른다
    }

    [Fact]
    public void IdleBotIsReplacedWhenDroppedFromTopCandidates()
    {
        At(9, 10);
        _host.CandidateList = new() { Day("A", 80) };
        _ap.OnTimer();
        Assert.Single(_host.BotList);

        _host.CandidateList = new() { Day("B", 75), Day("C", 74), Day("D", 73), Day("E", 72), Day("F", 71), Day("G", 70) };
        _host.Clock.Now = _host.Clock.Now.AddMinutes(31);
        _ap.OnTimer();
        Assert.DoesNotContain(_host.BotList, b => b.Symbol == "A");
        Assert.Equal(3, _host.BotList.Count);
    }

    [Fact]
    public void BlockReasonStopsNewSelections()
    {
        At(9, 10);
        _host.Block = "일 손실 한도";
        _host.CandidateList = new() { Day("A", 80) };
        _ap.OnTimer();
        Assert.Empty(_host.BotList);
        Assert.Contains("일 손실 한도", _ap.Status);
    }

    [Fact]
    public void WindDownFlattensDayBotsBeforeClosingPhase()
    {
        At(9, 10);
        _host.CandidateList = new() { Day("A", 80), Day("B", 70) };
        _ap.OnTimer();
        var holding = _host.BotList[0];
        Assert.True(holding.ManualBuy());
        holding.OnFill(_host.BotHost.Orders[0].Id, OrderSide.Buy, 10, 10_010m);
        holding.OnOrderDone(_host.BotHost.Orders[0].Id, OrderStatus.Filled, null);

        At(14, 50);
        _ap.OnTimer();
        var sell = _host.BotHost.Orders.Last();
        Assert.Equal(OrderSide.Sell, sell.Side);                                   // 보유분 정리
        Assert.Single(_host.BotList);                                               // 미보유 봇은 정리됨
        holding.OnFill(sell.Id, OrderSide.Sell, 10, 10_000m);
        holding.OnOrderDone(sell.Id, OrderStatus.Filled, null);
        Step();
        Assert.Empty(_host.BotList);                                                // 청산 후 정리
    }

    [Fact]
    public void ClosingPhaseSwitchesScannerAndPicksFullyPassingCandidates()
    {
        At(14, 41);
        _ap.OnTimer();
        Assert.Equal(ScanMode.ClosingBet, _host.Mode);

        At(14, 56);
        _host.CandidateList = new() { Closing("X", 6), Closing("Y", 5), Closing("Z", 6) };
        _ap.OnTimer();
        Assert.Equal(new[] { "X", "Z" }, _host.BotList.Select(b => b.Symbol));
        var bot = _host.BotList[0];
        Assert.Equal(AutoPilot.ClosingRole, bot.AutoRole);
        Assert.Equal(EntryStrategyKind.ClosingBet, bot.Settings.Strategy);
        Assert.True(bot.Settings.HoldOvernight);
        Assert.Equal(BotMode.FullAuto, bot.Settings.Mode);
    }

    [Fact]
    public void ClosingBotIsReplacedOnlyAfterRepeatedMisses()
    {
        At(14, 56);
        _host.CandidateList = new() { Closing("X", 6) };
        _ap.OnTimer();
        _host.CandidateList = new() { Closing("W", 6), Closing("Y", 6), Closing("X", 4) };  // X 탈락 (흔들림일 수 있음)
        Step();
        Assert.Contains(_host.BotList, b => b.Symbol == "X");
        Step();
        Assert.Contains(_host.BotList, b => b.Symbol == "X");
        Step();                                                                       // 3회 연속 탈락 → 교체
        Assert.DoesNotContain(_host.BotList, b => b.Symbol == "X");
        Assert.Equal(new[] { "W", "Y" }, _host.BotList.Select(b => b.Symbol).OrderBy(s => s));
    }

    [Fact]
    public void AfterCloseUnenteredClosingBotsAreRemovedButHoldingsKept()
    {
        At(14, 56);
        _host.CandidateList = new() { Closing("X", 6), Closing("Z", 6) };
        _ap.OnTimer();
        var held = _host.BotList[0];
        At(15, 10);
        _host.BotHost.Clock.Now = _host.Clock.Now;
        Assert.True(held.ManualBuy());
        held.OnFill(_host.BotHost.Orders.Last().Id, OrderSide.Buy, 10, 10_010m);
        held.OnOrderDone(_host.BotHost.Orders.Last().Id, OrderStatus.Filled, null);

        At(15, 21);
        _ap.OnTimer();
        var remaining = Assert.Single(_host.BotList);
        Assert.Same(held, remaining);
        Assert.Contains("종가 보유 1종목", _ap.Status);

        // 재시작 후에도 자동 운용 봇으로 인식
        var state = held.Capture();
        Assert.Equal(AutoPilot.ClosingRole, state.AutoRole);
        Assert.Equal(AutoPilot.ClosingRole, TradingBot.Restore(state, held.Context, _host.BotHost).AutoRole);
    }

    [Fact]
    public void ExistingBotsBlockSymbolAndDisablingStopsSelectionOnly()
    {
        At(9, 10);
        var other = _host.AddAutoBot("U", "기존", new BotSettings(), "x")!;
        other.AutoRole = null;
        _host.CandidateList = new() { Day("U", 90), Day("A", 80) };
        _ap.OnTimer();
        Assert.Equal(new[] { "U", "A" }, _host.BotList.Select(b => b.Symbol));      // U 는 이미 봇이 있어 건너뜀

        _ap.UpdatePlan(AutoPilotPlan.Default(enabled: false));
        var count = _host.BotList.Count;
        _host.CandidateList = new() { Day("B", 80) };
        Step();
        Assert.Equal(count, _host.BotList.Count);                                    // 새 종목은 안 고름
        Assert.Equal("꺼짐", _ap.Phase);

        other.Stop(flatten: false);                                                   // 꺼져 있어도 끝난 봇은 정리
        Step();
        Assert.DoesNotContain(other, _host.BotList);

        At(14, 41);
        _ap.OnTimer();
        Assert.Equal(ScanMode.ClosingBet, _host.Mode);                                // 스캐너 모드는 시간대대로
    }

    [Fact]
    public void PlanFallsBackToDefaultPresetsAndValidatesTimes()
    {
        var presets = new Dictionary<string, BotSettings> { ["ORB 표준"] = new BotSettings { Mode = BotMode.ManualEntry } };
        var plan = AutoPilotPlan.FromPresets(new AutoPilotSettings { MorningPreset = "ORB 표준", ClosingPreset = "없음" }, presets);
        Assert.Equal(EntryStrategyKind.OpeningRangeBreakout, plan.Morning.Strategy);  // 수동 전략 프리셋 → 기본값
        Assert.Equal(EntryStrategyKind.ClosingBet, plan.Closing.Strategy);
        Assert.Empty(new AutoPilotSettings().Validate());
        Assert.NotEmpty(new AutoPilotSettings { DayExitTime = new TimeOnly(15, 0) }.Validate());
    }
}
