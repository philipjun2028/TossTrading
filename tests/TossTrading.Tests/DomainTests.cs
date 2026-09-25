using TossTrading.Domain;
using TossTrading.Engine.Risk;
using TossTrading.Engine;

namespace TossTrading.Tests;

public class TickRulesTests
{
    [Theory]
    [InlineData(1_999, 1)]
    [InlineData(2_000, 5)]
    [InlineData(4_995, 5)]
    [InlineData(5_000, 10)]
    [InlineData(19_990, 10)]
    [InlineData(20_000, 50)]
    [InlineData(50_000, 100)]
    [InlineData(200_000, 500)]
    [InlineData(500_000, 1_000)]
    public void KrxTickSize(decimal price, decimal tick) => Assert.Equal(tick, TickRules.TickSize(price));

    [Theory]
    [InlineData(1_999, 1, 2_000)]
    [InlineData(2_000, 1, 2_005)]
    [InlineData(2_000, -1, 1_999)]
    [InlineData(5_000, -1, 4_995)]
    [InlineData(19_990, 2, 20_050)]
    [InlineData(10_000, -3, 9_970)]
    public void AddTicksCrossesBoundaries(decimal price, int ticks, decimal expected) =>
        Assert.Equal(expected, TickRules.AddTicks(price, ticks));

    [Fact]
    public void Rounding()
    {
        Assert.Equal(12_340m, TickRules.RoundDown(12_345m));
        Assert.Equal(12_350m, TickRules.RoundUp(12_345m));
        Assert.Equal(2_000m, TickRules.RoundUp(1_999.5m));
        Assert.Equal(5_000m, TickRules.RoundUp(4_999m));
        Assert.Equal(12_340m, TickRules.RoundUp(12_340m));
    }

    [Fact]
    public void TickCostRateMatchesDesignDocExamples()
    {
        Assert.Equal(0.0025m, TickRules.TickCostRate(2_000m));
        Assert.True(TickRules.TickCostRate(1_990m) < 0.0006m);
    }
}

public class CostModelTests
{
    private readonly CostModel _cost = new(new CostSettings());

    [Fact]
    public void RoundTripCostAt10000IsAbout043Percent() =>
        Assert.Equal(0.0043m, _cost.RoundTripCostRate(10_000m));

    [Fact]
    public void NetPnlIncludesCommissionAndTax()
    {
        // 10,000원 100주 매수 → 10,300원 매도
        // 총이익 30,000 − 매수수수료 150 − 매도수수료 154 − 세금 2,060 = 27,636
        Assert.Equal(27_636m, _cost.NetPnl(10_000m, 10_300m, 100));
    }

    [Fact]
    public void BreakEvenPriceGivesZeroNet()
    {
        var be = _cost.BreakEvenPrice(10_000m);
        Assert.InRange(be, 10_023m, 10_024m);
        Assert.True(_cost.NetPnl(10_000m, TickRules.RoundUp(be), 1_000) >= -2m);
    }
}

public class PositionSizerTests
{
    [Fact]
    public void RiskBasedSizing()
    {
        var s = new BotSettings { Sizing = SizingMode.RiskBased, RiskPerTradePct = 0.5m, MaxPositionAmount = 10_000_000m };
        // 계좌 1,000만 × 0.5% = 5만원 리스크, 주당 리스크 200원 → 250주 (설계 문서 7.1 예시)
        Assert.Equal(250m, PositionSizer.Quantity(s, 10_000_000m, 10_000m, 9_800m, 100_000_000m, 50_000_000m));
    }

    [Fact]
    public void CappedByMaxPositionAndBuyingPower()
    {
        var s = new BotSettings { Sizing = SizingMode.RiskBased, RiskPerTradePct = 0.5m, MaxPositionAmount = 2_000_000m };
        Assert.Equal(200m, PositionSizer.Quantity(s, 10_000_000m, 10_000m, 9_800m, 100_000_000m, 50_000_000m));
        Assert.Equal(50m, PositionSizer.Quantity(s, 10_000_000m, 10_000m, 9_800m, 500_000m, 50_000_000m));
    }

    [Fact]
    public void FixedAmountAndInvalidStop()
    {
        var s = new BotSettings { Sizing = SizingMode.FixedAmount, FixedAmount = 1_000_000m };
        Assert.Equal(100m, PositionSizer.Quantity(s, 0, 10_000m, 0, 5_000_000m, 50_000_000m));
        var r = new BotSettings { Sizing = SizingMode.RiskBased };
        Assert.Equal(0m, PositionSizer.Quantity(r, 10_000_000m, 10_000m, 10_100m, 5_000_000m, 50_000_000m));
    }
}

public class BotSettingsTests
{
    [Fact]
    public void DefaultsAndPresetsAreValid()
    {
        Assert.Empty(new BotSettings().Validate());
        foreach (var (name, preset) in BotPresets.CreateDefaults())
            Assert.True(preset.Validate().Count == 0, name);
    }

    [Fact]
    public void ManualModeWithStrategyIsRejected()
    {
        var s = new BotSettings { Mode = BotMode.ManualEntry, Strategy = EntryStrategyKind.OpeningRangeBreakout };
        Assert.NotEmpty(s.Validate());
    }
}

public class RiskManagerTests
{
    private static readonly DateTimeOffset T = Kst.At(new DateOnly(2026, 9, 25), new TimeOnly(10, 0));

    [Fact]
    public void BlocksOnMaxPositionsExposureAndOrderSize()
    {
        var r = new RiskManager(new RiskSettings { MaxConcurrentPositions = 2, MaxTotalExposurePct = 50m });
        r.SetStartEquity(10_000_000m);
        Assert.True(r.CanEnter(T, 1_000_000m, 1, 0, 0).Allowed);
        Assert.False(r.CanEnter(T, 1_000_000m, 2, 0, 0).Allowed);
        Assert.False(r.CanEnter(T, 2_000_000m, 0, 4_000_000m, 0).Allowed);
        Assert.False(r.CanEnter(T, 60_000_000m, 0, 0, 0).Allowed);
    }

    [Fact]
    public void DailyLossLimitTriggersOnce()
    {
        var r = new RiskManager(new RiskSettings { DailyLossLimitPct = 1.5m });
        r.SetStartEquity(10_000_000m);
        Assert.False(r.CheckDailyLimits(-100_000m));
        Assert.True(r.CheckDailyLimits(-150_000m));
        Assert.False(r.CheckDailyLimits(-200_000m));
        Assert.False(r.CanEnter(T, 1_000m, 0, 0, 0).Allowed);
    }

    [Fact]
    public void ConsecutiveLossesStartCooldown()
    {
        var r = new RiskManager(new RiskSettings { MaxConsecutiveLosses = 2, ConsecutiveLossCooldownMinutes = 30 });
        r.SetStartEquity(10_000_000m);
        var loss = new ClosedTrade("b", "s", "n", "x", ExecutionMode.Paper, T, T, 1, 1, 1, -1_000m, -1, null, "", "");
        r.OnTradeClosed(loss, T);
        Assert.True(r.CanEnter(T, 1_000m, 0, 0, 0).Allowed);
        r.OnTradeClosed(loss, T);
        Assert.False(r.CanEnter(T.AddMinutes(10), 1_000m, 0, 0, 0).Allowed);
        Assert.True(r.CanEnter(T.AddMinutes(31), 1_000m, 0, 0, 0).Allowed);
    }

    [Fact]
    public void KillSwitchBlocksEntries()
    {
        var r = new RiskManager(new RiskSettings());
        r.SetStartEquity(10_000_000m);
        r.ActivateKillSwitch();
        Assert.False(r.CanEnter(T, 1_000m, 0, 0, 0).Allowed);
        r.ResetKillSwitch();
        Assert.True(r.CanEnter(T, 1_000m, 0, 0, 0).Allowed);
    }
}

public class RateGateTests
{
    [Fact]
    public async Task LimitsCallsPerSecond()
    {
        long now = 0;
        var gate = new RateGate(() => 3, () => now);
        for (var i = 0; i < 3; i++) await gate.WaitAsync(TestContext.Current.CancellationToken);
        var fourth = gate.WaitAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(fourth.IsCompleted);
        now = 1_001;
        await fourth.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }
}
