using TossTrading.Domain;
using TossTrading.Engine;
using TossTrading.Engine.Infrastructure;
using TossTrading.Engine.Market;
using TossTrading.Engine.Paper;
using TossTrading.Engine.Simulation;
using TossTrading.Engine.Strategies;
using TossTrading.Engine.Trading;

namespace TossTrading.Tests;

public class ClosingBetSignalTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);

    /// <summary>+8% 상승 후 고가 부근에서 거래되는 종목 (전일 종가 10,000)</summary>
    private static SymbolContext StrongCloser(TimeOnly until)
    {
        var ctx = new SymbolContext("000001", "테스트") { PreviousClose = 10_000m };
        ctx.OnTrade(new TradeTick("000001", 10_000m, 1, Kst.At(Day, new TimeOnly(9, 0)))); // 당일 저가
        var t = Kst.At(Day, new TimeOnly(9, 0, 30));
        var end = Kst.At(Day, until);
        var price = 10_300m;
        while (t < end)
        {
            price = Math.Min(10_800m, price + 10m);
            ctx.OnTrade(new TradeTick("000001", price, 100, t));
            t = t.AddMinutes(1);
        }
        return ctx;
    }

    [Fact]
    public void FiresOncePerDayInsideClosingWindow()
    {
        var s = BotPresets.CreateDefaults()["종가베팅 (익일 매도)"];
        var sig = new ClosingBetSignal();

        var ctx = StrongCloser(new TimeOnly(14, 50));
        Assert.Null(sig.Evaluate(ctx, Kst.At(Day, new TimeOnly(14, 50)), s, SignalTrigger.Trade)); // 15:00 이전

        ctx = StrongCloser(new TimeOnly(15, 5));
        var fired = sig.Evaluate(ctx, Kst.At(Day, new TimeOnly(15, 5)), s, SignalTrigger.Trade);
        Assert.NotNull(fired);
        Assert.Null(fired!.StructuralStop);
        Assert.Null(sig.Evaluate(ctx, Kst.At(Day, new TimeOnly(15, 6)), s, SignalTrigger.Trade)); // 하루 한 번
    }

    [Fact]
    public void RejectsWeakCloseAndNearUpperLimit()
    {
        var s = BotPresets.CreateDefaults()["종가베팅 (익일 매도)"];
        var now = Kst.At(Day, new TimeOnly(15, 5));

        // 고가 11,500 찍고 10,500 으로 밀린 종목: 범위 위치 낮음
        var weak = StrongCloser(new TimeOnly(15, 0));
        weak.OnTrade(new TradeTick("000001", 11_500m, 10, Kst.At(Day, new TimeOnly(14, 0))));
        weak.OnTrade(new TradeTick("000001", 10_500m, 10, now));
        Assert.Null(new ClosingBetSignal().Evaluate(weak, now, s, SignalTrigger.Trade));

        var nearLimit = StrongCloser(new TimeOnly(15, 5));
        nearLimit.Limits = new PriceLimits(11_000m, 7_000m);
        Assert.Null(new ClosingBetSignal().Evaluate(nearLimit, now, s, SignalTrigger.Trade));
    }

    [Fact]
    public void ClosingBetRequiresHoldOvernight()
    {
        var s = new BotSettings { Mode = BotMode.SemiAuto, Strategy = EntryStrategyKind.ClosingBet };
        Assert.Contains(s.Validate(), e => e.Contains("익일 보유"));
    }
}

public class OvernightBotTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);
    private readonly FakeHost _host = new();
    private readonly SymbolContext _ctx = new("000001", "테스트");

    private void Trade(decimal p)
    {
        _ctx.OnOrderBook(new OrderBookSnapshot("000001", _host.Now,
            new[] { new PriceLevel(TickRules.AddTicks(p, 1), 100) }, new[] { new PriceLevel(p, 100) }));
        _ctx.OnTrade(new TradeTick("000001", p, 10, _host.Now));
    }

    private TradingBot HoldingBot(NextDayExitMode mode)
    {
        _host.Clock.Now = Kst.At(Day, new TimeOnly(15, 5));
        Trade(10_000m);
        var s = BotPresets.CreateDefaults()["종가베팅 (익일 매도)"];
        s.Mode = BotMode.ManualEntry;
        s.Strategy = EntryStrategyKind.Manual;
        s.NextDayExitMode = mode;
        var bot = new TradingBot("b1", _ctx, s, _host);
        bot.Start();
        Assert.True(bot.ManualBuy());
        bot.OnFill(_host.Orders[0].Id, OrderSide.Buy, 100, 10_010m);
        bot.OnOrderDone(_host.Orders[0].Id, OrderStatus.Filled, null);
        return bot;
    }

    [Fact]
    public void NoForcedExitAtCloseAndNoExitsOutsideRegularSession()
    {
        var bot = HoldingBot(NextDayExitMode.Managed);

        _host.Clock.Now = Kst.At(Day, new TimeOnly(15, 11)); // 당일 봇이면 15:10 강제청산
        Trade(10_000m);
        bot.OnTimer();
        Assert.Single(_host.Orders);

        _host.Clock.Now = Kst.At(Day, new TimeOnly(16, 0));  // NXT 시간외 급락 체결이 와도 손절 안 함
        Trade(9_500m);
        bot.OnMarket(SignalTrigger.Trade);
        bot.OnTimer();
        Assert.Single(_host.Orders);
        Assert.Contains("익일 보유", bot.StateReason);
        Assert.Equal(BotState.InPosition, bot.State);
    }

    [Fact]
    public void EntryDayAppliesOnlyStopLoss()
    {
        var bot = HoldingBot(NextDayExitMode.Managed);
        _host.Clock.Now = Kst.At(Day, new TimeOnly(15, 10));
        Trade(10_600m); // +6%: 익절 조건이지만 당일은 보유
        bot.OnMarket(SignalTrigger.Trade);
        Assert.Single(_host.Orders);

        Trade(9_700m);  // 손절 (-3%) 은 당일에도 적용
        bot.OnMarket(SignalTrigger.Trade);
        Assert.Contains("손절", _host.Orders[1].Reason);
    }

    [Fact]
    public void TakeProfitAppliesNextDay()
    {
        var bot = HoldingBot(NextDayExitMode.Managed);
        _host.Clock.Now = Kst.At(Day.AddDays(1), new TimeOnly(9, 1));
        Trade(10_600m);
        bot.OnMarket(SignalTrigger.Trade);
        Assert.Contains("목표 익절", _host.Orders[1].Reason);
    }

    [Fact]
    public void AtOpenModeSellsAtMarketOnFirstTradeNextDay()
    {
        var bot = HoldingBot(NextDayExitMode.AtOpen);
        var next = Day.AddDays(1);

        _host.Clock.Now = Kst.At(next, new TimeOnly(8, 55)); // 장 전: 아무것도 안 함
        bot.OnTimer();
        Assert.Single(_host.Orders);

        _host.Clock.Now = Kst.At(next, new TimeOnly(9, 0, 5));
        Trade(10_300m);
        bot.OnMarket(SignalTrigger.Trade);
        var exit = _host.Orders[1];
        Assert.Equal(OrderSide.Sell, exit.Side);
        Assert.Equal(OrderType.Market, exit.Type);
        Assert.Equal(100m, exit.Qty);
        Assert.Contains("시초", exit.Reason);
    }

    [Fact]
    public void ManagedModeUsesStopsThenSellsAtDeadline()
    {
        var bot = HoldingBot(NextDayExitMode.Managed);
        var next = Day.AddDays(1);

        _host.Clock.Now = Kst.At(next, new TimeOnly(9, 30));
        Trade(10_050m);
        bot.OnMarket(SignalTrigger.Trade);
        Assert.Single(_host.Orders); // 조건 없음 → 보유 유지

        _host.Clock.Now = Kst.At(next, new TimeOnly(10, 0, 1));
        Trade(10_060m);
        bot.OnTimer();
        Assert.Contains("익일 청산 시각", _host.Orders[1].Reason);
    }

    [Fact]
    public void ManagedModeGapDownHitsStopNextDay()
    {
        var bot = HoldingBot(NextDayExitMode.Managed);
        _host.Clock.Now = Kst.At(Day.AddDays(1), new TimeOnly(9, 0, 3));
        Trade(9_600m); // 손절 -3% (9,710) 아래로 갭 하락
        bot.OnMarket(SignalTrigger.Trade);
        Assert.Equal(OrderPriority.Emergency, _host.Orders[1].Prio);
        Assert.Contains("손절", _host.Orders[1].Reason);
    }

    [Fact]
    public void CaptureAndRestoreOnNextDayKeepsPosition()
    {
        var bot = HoldingBot(NextDayExitMode.Managed);
        var state = bot.Capture();

        _host.Clock.Now = Kst.At(Day.AddDays(1), new TimeOnly(8, 50));
        var restored = TradingBot.Restore(state, new SymbolContext("000001", "테스트"), _host);
        Assert.Equal(100m, restored.Quantity);
        Assert.Equal(10_010m, restored.AveragePrice);
        Assert.Equal(bot.StopPrice, restored.StopPrice);
        Assert.Equal(BotState.InPosition, restored.State);
        Assert.Equal(1, restored.Entries);
        Assert.True(restored.IsCarriedOver);
    }
}

public class PersistenceTests
{
    [Fact]
    public async Task PaperAccountRoundTrip()
    {
        var clock = new ManualClock();
        var a = new PaperBroker(10_000_000m, new CostModel(new CostSettings()), clock);
        a.OnOrderBook(new OrderBookSnapshot("A", clock.Now, new[] { new PriceLevel(10_000m, 1_000) }, new[] { new PriceLevel(9_990m, 1_000) }));
        await a.PlaceOrderAsync(new OrderRequest("c1", "A", OrderSide.Buy, OrderType.Limit, 10, 10_000m, OrderPriority.Entry, null, "t"),
            TestContext.Current.CancellationToken);

        var b = new PaperBroker(1m, new CostModel(new CostSettings()), clock);
        b.RestoreState(a.CaptureState());
        var snap = await b.GetAccountSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.Equal(10_000_000m - 100_000m - 15m, snap.Cash);
        Assert.Equal(10m, snap.Holdings.Single().Quantity);
    }

    [Fact]
    public async Task EngineRestoresBotAndPaperPositionAfterRestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tt-state-" + Guid.NewGuid().ToString("N"));
        var ct = TestContext.Current.CancellationToken;
        try
        {
            string symbol;
            {
                var sim = new SimulatedMarket(new SimulationOptions { Seed = 3, ManualClock = true });
                var options = new EngineOptions { RunScanner = false, StateDirectory = dir };
                var paper = new PaperBroker(10_000_000m, new CostModel(options.Cost), sim);
                await using var engine = new TradingEngine(options, sim, sim, paper, sim);
                await engine.StartAsync(ct);
                symbol = sim.Symbols[0];
                sim.AdvanceTo(sim.Now.AddMinutes(10));
                var id = await engine.AddBotAsync(symbol, null, new BotSettings { Sizing = SizingMode.FixedAmount, FixedAmount = 1_000_000m });
                await engine.StartBotAsync(id);
                for (var i = 0; i < 5; i++) { sim.AdvanceTo(sim.Now.AddSeconds(5)); await engine.TickAsync(); await Task.Delay(50, ct); }
                await engine.ManualBuyAsync(id);
                for (var i = 0; i < 100 && (engine.Snapshot.Bots.Count == 0 || engine.Snapshot.Bots[0].Quantity == 0); i++)
                {
                    await engine.TickAsync();
                    await Task.Delay(30, ct);
                }
                Assert.True(engine.Snapshot.Bots[0].Quantity > 0);
            } // DisposeAsync → 최종 저장

            Assert.True(File.Exists(Path.Combine(dir, "bots_paper.json")));
            Assert.True(File.Exists(Path.Combine(dir, "paper_account.json")));

            var sim2 = new SimulatedMarket(new SimulationOptions { Seed = 3, ManualClock = true });
            var options2 = new EngineOptions { RunScanner = false, StateDirectory = dir };
            var paper2 = new PaperBroker(10_000_000m, new CostModel(options2.Cost), sim2);
            await using var engine2 = new TradingEngine(options2, sim2, sim2, paper2, sim2);
            await engine2.StartAsync(ct);
            await engine2.TickAsync();
            var bot = Assert.Single(engine2.Snapshot.Bots);
            Assert.Equal(symbol, bot.Symbol);
            Assert.True(bot.Quantity > 0);
            Assert.Equal(BotState.InPosition, bot.State);
            var holdings = (await paper2.GetAccountSnapshotAsync(ct)).Holdings;
            Assert.Equal(bot.Quantity, holdings.Single().Quantity);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}

public class ClosingScannerTests
{
    private static readonly ScannerSettings S = new() { Mode = ScanMode.ClosingBet };

    [Fact]
    public void EvaluateClosingMarksEachCondition()
    {
        var good = TossTrading.Engine.Scanning.ScannerService.EvaluateClosing(8m, 0.9m, 1.5m, 0.8m, 130m, 30_000_000_000m, S);
        Assert.True(good.AllPassed);
        Assert.Equal(6, good.Passed);
        Assert.Equal("등락✔ 고가권✔ VWAP✔ 30분✔ 강도✔ 상한가✔", good.Checks);

        var fading = TossTrading.Engine.Scanning.ScannerService.EvaluateClosing(8m, 0.4m, -0.5m, -1.2m, null, 30_000_000_000m, S);
        Assert.False(fading.AllPassed);
        Assert.Equal("등락✔ 고가권✖ VWAP✖ 30분✖ 강도? 상한가✔", fading.Checks);
        Assert.Equal(5, fading.Total); // 강도는 데이터 없음 → 집계 제외
        Assert.True(fading.Score < good.Score);
    }

    [Fact]
    public void ComputeIntradayUsesBarsAndThirtyMinuteReference()
    {
        var day = new DateOnly(2026, 9, 25);
        var bars = Enumerable.Range(0, 60).Select(i => new Bar
        {
            Start = Kst.At(day, new TimeOnly(14, 0).AddMinutes(i)),
            Open = 10_000m + i * 10, High = 10_010m + i * 10, Low = 9_990m + i * 10, Close = 10_000m + i * 10, Volume = 100,
        }).ToList();
        var now = Kst.At(day, new TimeOnly(15, 0));
        var st = TossTrading.Engine.Scanning.ScannerService.ComputeIntraday(bars, now)!;
        Assert.Equal(10_600m, st.High);
        Assert.Equal(9_990m, st.Low);
        Assert.Equal(10_300m, st.Close30mAgo); // 14:30 봉 종가
        Assert.Equal(1m, TossTrading.Engine.Scanning.ScannerService.RangePositionOf(10_600m, st));
    }

    [Fact]
    public void ComputeIntradayOnClosedDayUsesLastSession()
    {
        var friday = new DateOnly(2026, 9, 25);
        var bars = Enumerable.Range(0, 390).Select(i => new Bar
        {
            Start = Kst.At(friday, new TimeOnly(9, 0).AddMinutes(i)),
            Open = 10_000m, High = 10_000m + i, Low = 10_000m, Close = 10_000m + i, Volume = 10,
        }).ToList();
        var saturday = Kst.At(friday.AddDays(1), new TimeOnly(11, 0));
        var st = TossTrading.Engine.Scanning.ScannerService.ComputeIntraday(bars, saturday)!;
        Assert.Equal(friday, st.SessionDate);
        Assert.Equal(10_360m, st.Close30mAgo); // 장 마감(15:30) 기준 30분 전 = 15:00 봉
    }

    [Fact]
    public async Task ClosingModeEvaluatesAllCandidatesWithoutLiveData()
    {
        var sim = new SimulatedMarket(new SimulationOptions { Seed = 42, ManualClock = true, StartTime = new TimeOnly(9, 0) });
        sim.AdvanceTo(Kst.At(Kst.DateOf(sim.Now), new TimeOnly(15, 0)));
        var settings = new ScannerSettings { Mode = ScanMode.ClosingBet, ClosingBarsPerCycle = 5, ClosingMinChangePct = 3m, ClosingMaxChangePct = 20m };
        var scanner = new TossTrading.Engine.Scanning.ScannerService(sim, sim, settings, _ => null, _ => { }, (_, _) => { });

        IReadOnlyList<ScanCandidate> result = Array.Empty<ScanCandidate>();
        for (var i = 0; i < 10; i++) result = await scanner.ScanOnceAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(result);
        Assert.All(result, c =>
        {
            Assert.NotNull(c.ClosingChecks);
            Assert.NotNull(c.RangePosition);   // 실시간 구독 없이도 분봉으로 계산
            Assert.NotNull(c.VwapDistPct);
            Assert.InRange(c.ChangePct, 3m, 20m);
        });
        var firstFail = result.ToList().FindIndex(c => !c.Tags.Contains("종가후보"));
        if (firstFail >= 0) Assert.DoesNotContain(result.Skip(firstFail), c => c.Tags.Contains("종가후보")); // 통과 종목이 위로
    }
}
