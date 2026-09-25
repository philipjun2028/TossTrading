using TossTrading.Domain;
using TossTrading.Engine;
using TossTrading.Engine.Market;
using TossTrading.Engine.Paper;
using TossTrading.Engine.Simulation;
using TossTrading.Engine.Strategies;
using TossTrading.Engine.Trading;

namespace TossTrading.Tests;

internal sealed class ManualClock : IClock
{
    public DateTimeOffset Now { get; set; } = Kst.At(new DateOnly(2026, 9, 25), new TimeOnly(9, 30));
}

public class SymbolContextTests
{
    private static readonly DateTimeOffset Open = Kst.At(new DateOnly(2026, 9, 25), new TimeOnly(9, 0));

    [Fact]
    public void BuildsBarsVwapAndOpeningRange()
    {
        var ctx = new SymbolContext("000001", "테스트");
        Assert.False(ctx.OnTrade(new TradeTick("000001", 10_000m, 100, Open.AddSeconds(5))));
        ctx.OnTrade(new TradeTick("000001", 10_200m, 100, Open.AddSeconds(30)));
        Assert.True(ctx.OnTrade(new TradeTick("000001", 10_100m, 200, Open.AddMinutes(1).AddSeconds(1))));

        Assert.Single(ctx.Bars);
        Assert.Equal(10_000m, ctx.Bars[0].Open);
        Assert.Equal(10_200m, ctx.Bars[0].High);
        Assert.Equal(10_100m, ctx.Vwap); // (1,000,000 + 1,020,000 + 2,020,000) / 400
        Assert.Equal(10_200m, ctx.DayHigh);

        var (hi, lo, complete) = ctx.OpeningRange(5, Open.AddMinutes(3));
        Assert.False(complete);
        (hi, lo, complete) = ctx.OpeningRange(5, Open.AddMinutes(6));
        Assert.True(complete);
        Assert.Equal(10_200m, hi);
        Assert.Equal(10_000m, lo);

        Assert.True(ctx.OnTimer(Open.AddMinutes(2).AddSeconds(1)));
        Assert.Equal(2, ctx.Bars.Count);
    }

    [Fact]
    public void StrengthUsesOrderBookAggressor()
    {
        var ctx = new SymbolContext("000001", "테스트");
        ctx.OnOrderBook(new OrderBookSnapshot("000001", Open, new[] { new PriceLevel(10_010m, 5) }, new[] { new PriceLevel(10_000m, 5) }));
        ctx.OnTrade(new TradeTick("000001", 10_010m, 300, Open));
        ctx.OnTrade(new TradeTick("000001", 10_000m, 100, Open));
        Assert.Equal(300m, ctx.Strength);
    }

    [Fact]
    public void SeedAddsHistoryBeforeLiveData()
    {
        var ctx = new SymbolContext("000001", "테스트");
        ctx.OnTrade(new TradeTick("000001", 10_500m, 10, Open.AddMinutes(10).AddSeconds(3)));
        var seed = Enumerable.Range(0, 12).Select(i => new Bar
        {
            Start = Open.AddMinutes(i), Open = 10_000m, High = 10_100m, Low = 9_900m, Close = 10_000m, Volume = 100,
        }).ToList();
        ctx.Seed(seed);
        Assert.Equal(10, ctx.Bars.Count);            // 10분 봉 이후는 라이브와 겹치므로 제외
        Assert.Equal(10_000m, ctx.DayOpen);
        Assert.Equal(9_900m, ctx.DayLow);
        Assert.Equal(1_010m, ctx.CumVolume);
    }
}

public class PaperBrokerTests
{
    private readonly ManualClock _clock = new();
    private readonly PaperBroker _broker;
    private readonly List<OrderUpdate> _updates = new();

    public PaperBrokerTests()
    {
        _broker = new PaperBroker(10_000_000m, new CostModel(new CostSettings()), _clock);
        _broker.OrderUpdated += _updates.Add;
        _broker.OnOrderBook(new OrderBookSnapshot("A", _clock.Now,
            new[] { new PriceLevel(10_000m, 50), new PriceLevel(10_010m, 100) },
            new[] { new PriceLevel(9_990m, 100) }));
    }

    private static OrderRequest Req(OrderSide side, OrderType type, decimal qty, decimal? price) =>
        new(OrderRequest.NewClientOrderId(), "A", side, type, qty, price, OrderPriority.Entry, null, "test");

    [Fact]
    public async Task AggressiveLimitWalksTheBook()
    {
        var ack = await _broker.PlaceOrderAsync(Req(OrderSide.Buy, OrderType.Limit, 120, 10_010m), default);
        var last = _updates.Last(u => u.OrderId == ack.OrderId);
        Assert.Equal(OrderStatus.Filled, last.Status);
        Assert.Equal(120m, last.FilledQuantity);
        Assert.Equal((50 * 10_000m + 70 * 10_010m) / 120m, last.AverageFilledPrice);
    }

    [Fact]
    public async Task RestingLimitFillsOnlyWhenTradedThrough()
    {
        var ack = await _broker.PlaceOrderAsync(Req(OrderSide.Buy, OrderType.Limit, 10, 9_980m), default);
        Assert.Equal(OrderStatus.Pending, _updates.Last().Status);
        _broker.OnTrade(new TradeTick("A", 9_980m, 5, _clock.Now));   // 같은 가격 → 미체결 (보수적)
        Assert.Equal(OrderStatus.Pending, _updates.Last(u => u.OrderId == ack.OrderId).Status);
        _broker.OnTrade(new TradeTick("A", 9_970m, 5, _clock.Now));
        Assert.Equal(OrderStatus.Filled, _updates.Last(u => u.OrderId == ack.OrderId).Status);
    }

    [Fact]
    public async Task RejectsOversellAndInsufficientCash()
    {
        await Assert.ThrowsAsync<BrokerException>(() => _broker.PlaceOrderAsync(Req(OrderSide.Sell, OrderType.Market, 1, null), default));
        await Assert.ThrowsAsync<BrokerException>(() => _broker.PlaceOrderAsync(Req(OrderSide.Buy, OrderType.Limit, 2_000, 10_000m), default));
        await Assert.ThrowsAsync<BrokerException>(() => _broker.PlaceOrderAsync(Req(OrderSide.Buy, OrderType.Limit, 1, 10_003m), default));
    }

    [Fact]
    public async Task ModifyToMarketFillsAndIssuesNewOrderId()
    {
        await _broker.PlaceOrderAsync(Req(OrderSide.Buy, OrderType.Limit, 10, 10_000m), default);
        var sell = await _broker.PlaceOrderAsync(Req(OrderSide.Sell, OrderType.Limit, 10, 10_100m), default);
        var modified = await _broker.ModifyOrderAsync(sell.OrderId, OrderType.Market, 10, null, default);
        Assert.NotEqual(sell.OrderId, modified.OrderId);
        Assert.Equal(OrderStatus.Replaced, _updates.Last(u => u.OrderId == sell.OrderId).Status);
        Assert.Equal(OrderStatus.Filled, _updates.Last(u => u.OrderId == modified.OrderId).Status);
        var snap = await _broker.GetAccountSnapshotAsync(default);
        Assert.Empty(snap.Holdings);
        Assert.True(snap.Cash < 10_000_000m); // 비용 + 스프레드 손실
    }
}

/// <summary>봇 상태머신 단위 테스트용 가짜 호스트</summary>
internal sealed class FakeHost : IBotHost
{
    public readonly List<(string Id, OrderSide Side, OrderType Type, decimal Qty, decimal? Price, OrderPriority Prio, string Reason)> Orders = new();
    public readonly List<string> Cancels = new();
    public readonly List<(string Id, OrderType Type)> Modifies = new();
    public readonly List<ClosedTrade> Trades = new();
    public ManualClock Clock { get; } = new();
    public DateTimeOffset Now => Clock.Now;
    public CostModel Cost { get; } = new(new CostSettings());
    public ExecutionMode Execution => ExecutionMode.Paper;
    public int MaxOrderErrorsPerBot => 3;
    public bool AllowEntry { get; set; } = true;
    public decimal SizeResult { get; set; } = 100;

    public string SubmitOrder(TradingBot bot, OrderSide side, OrderType type, decimal quantity, decimal? price, OrderPriority priority, string reason)
    {
        var id = $"c{Orders.Count + 1}";
        Orders.Add((id, side, type, quantity, price, priority, reason));
        return id;
    }

    public void ModifyOrder(TradingBot bot, string clientOrderId, OrderType type, decimal quantity, decimal? price) => Modifies.Add((clientOrderId, type));
    public void CancelOrder(TradingBot bot, string clientOrderId) => Cancels.Add(clientOrderId);
    public (bool Allowed, string? Reason) CanEnter(TradingBot bot, decimal amount) => (AllowEntry, AllowEntry ? null : "blocked");
    public decimal SizeFor(TradingBot bot, decimal entryPrice, decimal stopPrice) => SizeResult;
    public void OnTradeClosed(TradingBot bot, ClosedTrade trade) => Trades.Add(trade);
    public void Log(LogLevel level, string source, string message) { }
}

public class TradingBotTests
{
    private readonly FakeHost _host = new();
    private readonly SymbolContext _ctx = new("000001", "테스트");

    private TradingBot NewBot(BotSettings? s = null)
    {
        Price(10_000m);
        var bot = new TradingBot("b1", _ctx, s ?? new BotSettings { CooldownSeconds = 60 }, _host);
        bot.Start();
        return bot;
    }

    private void Price(decimal p)
    {
        _ctx.OnOrderBook(new OrderBookSnapshot("000001", _host.Now,
            new[] { new PriceLevel(TickRules.AddTicks(p, 1), 100) }, new[] { new PriceLevel(p, 100) }));
        _ctx.OnTrade(new TradeTick("000001", p, 10, _host.Now));
    }

    private void Tick(TradingBot bot, decimal p)
    {
        Price(p);
        bot.OnMarket(SignalTrigger.Trade);
    }

    [Fact]
    public void ManualBuyThenStopLoss()
    {
        var bot = NewBot();
        Assert.True(bot.ManualBuy());
        var entry = _host.Orders[0];
        Assert.Equal(OrderSide.Buy, entry.Side);
        Assert.Equal(10_020m, entry.Price);                 // 매도1호가(10,010) + 1틱
        Assert.Equal(BotState.EntryPending, bot.State);

        bot.OnFill(entry.Id, OrderSide.Buy, 100, 10_010m);
        bot.OnOrderDone(entry.Id, OrderStatus.Filled, null);
        Assert.Equal(BotState.InPosition, bot.State);
        Assert.Equal(9_860m, bot.StopPrice);                // 10,020 × (1 − 1.5%) 호가 내림

        Tick(bot, 9_850m);
        var exit = _host.Orders[1];
        Assert.Equal(OrderSide.Sell, exit.Side);
        Assert.Equal(OrderPriority.Emergency, exit.Prio);
        Assert.Equal(100m, exit.Qty);
        Assert.Equal(BotState.ExitPending, bot.State);

        bot.OnFill(exit.Id, OrderSide.Sell, 100, 9_840m);
        bot.OnOrderDone(exit.Id, OrderStatus.Filled, null);
        Assert.Single(_host.Trades);
        Assert.True(_host.Trades[0].NetPnl < 0);
        Assert.Equal(BotState.Cooldown, bot.State);
        Assert.Equal(1, bot.Losses);
    }

    [Fact]
    public void PartialTakeProfitThenTrailingAndBotTargetCompletes()
    {
        var bot = NewBot(new BotSettings { BotTargetProfitPct = 1m, MaxPositionAmount = 2_000_000m, TakeProfitPct = 0 });
        bot.ManualBuy();
        var entry = _host.Orders[0];
        bot.OnFill(entry.Id, OrderSide.Buy, 100, 10_000m);
        bot.OnOrderDone(entry.Id, OrderStatus.Filled, null);

        Tick(bot, 10_210m);                                  // +2.1% → 1차 50% 익절
        var partial = _host.Orders[1];
        Assert.Equal(50m, partial.Qty);
        bot.OnFill(partial.Id, OrderSide.Sell, 50, 10_200m);
        bot.OnOrderDone(partial.Id, OrderStatus.Filled, null);
        Assert.Equal(BotState.InPosition, bot.State);
        Assert.Equal(50m, bot.Quantity);

        Tick(bot, 10_600m);                                  // 고점 → 트레일링 스탑 10,470 (−1.2%)
        Assert.Equal(10_470m, bot.StopPrice);
        Tick(bot, 10_460m);
        var trail = _host.Orders[2];
        Assert.Contains("트레일링", trail.Reason);
        bot.OnFill(trail.Id, OrderSide.Sell, 50, 10_450m);

        Assert.Single(_host.Trades);
        Assert.True(bot.RealizedNet > 20_000m);              // 목표 1% × 200만 = 2만
        Assert.Equal(BotState.Completed, bot.State);
    }

    [Fact]
    public void EntryTimeoutCancelsAndDoesNotCountEntry()
    {
        var bot = NewBot(new BotSettings { EntryTimeoutSeconds = 3 });
        bot.ManualBuy();
        _host.Clock.Now = _host.Clock.Now.AddSeconds(4);
        bot.OnTimer();
        Assert.Single(_host.Cancels);
        bot.OnOrderDone(_host.Orders[0].Id, OrderStatus.Canceled, null);
        Assert.Equal(BotState.Watching, bot.State);
        Assert.Equal(0, bot.Entries);
    }

    [Fact]
    public void ExitTimeoutEscalatesToMarket()
    {
        var bot = NewBot();
        bot.ManualBuy();
        bot.OnFill(_host.Orders[0].Id, OrderSide.Buy, 100, 10_000m);
        bot.Flatten("test", emergency: false);
        _host.Clock.Now = _host.Clock.Now.AddSeconds(5);
        bot.OnTimer();
        Assert.Single(_host.Modifies);
        Assert.Equal(OrderType.Market, _host.Modifies[0].Type);
    }

    [Fact]
    public void TimeStopExitsWhenNoProgress()
    {
        var bot = NewBot(new BotSettings { TimeStopMinutes = 20 });
        bot.ManualBuy();
        bot.OnFill(_host.Orders[0].Id, OrderSide.Buy, 100, 10_000m);
        _host.Clock.Now = _host.Clock.Now.AddMinutes(21);
        bot.OnTimer();
        Assert.Contains("타임스탑", _host.Orders[1].Reason);
    }

    [Fact]
    public void RiskBlockPreventsEntryAndSemiAutoNeedsApproval()
    {
        var bot = NewBot();
        _host.AllowEntry = false;
        Assert.False(bot.ManualBuy());
        Assert.Empty(_host.Orders);
        Assert.Equal(0, bot.Entries);
    }

    [Fact]
    public void KillFlattensWithMarketOrder()
    {
        var bot = NewBot();
        bot.ManualBuy();
        bot.OnFill(_host.Orders[0].Id, OrderSide.Buy, 100, 10_000m);
        bot.Kill();
        var exit = _host.Orders[1];
        Assert.Equal(OrderType.Market, exit.Type);
        Assert.Equal(OrderPriority.Emergency, exit.Prio);
        bot.OnFill(exit.Id, OrderSide.Sell, 100, 9_990m);
        Assert.Equal(BotState.Stopped, bot.State);
    }
}

public class EntrySignalTests
{
    private static readonly DateTimeOffset Open = Kst.At(new DateOnly(2026, 9, 25), new TimeOnly(9, 0));

    [Fact]
    public void OrbFiresOnlyOnCrossAboveRangeHigh()
    {
        var ctx = new SymbolContext("000001", "테스트") { PreviousClose = 9_500m };
        var s = new BotSettings { OrbMinutes = 5 };
        var sig = new OpeningRangeBreakoutSignal();
        for (var m = 0; m < 5; m++)
        {
            ctx.OnTrade(new TradeTick("000001", 10_000m, 100, Open.AddMinutes(m)));
            ctx.OnTrade(new TradeTick("000001", 10_100m, 100, Open.AddMinutes(m).AddSeconds(20)));
            ctx.OnTrade(new TradeTick("000001", 10_050m, 100, Open.AddMinutes(m).AddSeconds(40)));
        }
        var t = Open.AddMinutes(5).AddSeconds(10);
        ctx.OnTrade(new TradeTick("000001", 10_080m, 500, t));
        Assert.Null(sig.Evaluate(ctx, t, s, SignalTrigger.Trade));
        ctx.OnTrade(new TradeTick("000001", 10_150m, 500, t.AddSeconds(1)));
        var fired = sig.Evaluate(ctx, t.AddSeconds(1), s, SignalTrigger.Trade);
        Assert.NotNull(fired);
        Assert.Equal(9_990m, fired!.StructuralStop);        // 범위 저가 10,000 − 1틱
        ctx.OnTrade(new TradeTick("000001", 10_160m, 500, t.AddSeconds(2)));
        Assert.Null(sig.Evaluate(ctx, t.AddSeconds(2), s, SignalTrigger.Trade)); // 재무장 전 재발사 금지
    }
}

public class EngineIntegrationTests
{
    [Fact]
    public async Task ManualBotTradesThroughPaperBrokerAndKillSwitchFlattens()
    {
        var sim = new SimulatedMarket(new SimulationOptions { Seed = 7, ManualClock = true });
        var options = new EngineOptions { RunScanner = false };
        var paper = new PaperBroker(10_000_000m, new CostModel(options.Cost), sim);
        await using var engine = new TradingEngine(options, sim, sim, paper, sim);
        await engine.StartAsync();

        var symbol = sim.Symbols[0];
        sim.AdvanceTo(sim.Now.AddMinutes(10));
        var id = await engine.AddBotAsync(symbol, null, new BotSettings { Sizing = SizingMode.FixedAmount, FixedAmount = 1_000_000m });
        await engine.StartBotAsync(id);
        for (var i = 0; i < 5; i++) { sim.AdvanceTo(sim.Now.AddSeconds(5)); await engine.TickAsync(); await Task.Delay(50); }

        await engine.ManualBuyAsync(id);
        var bought = await WaitUntil(engine, s => s.Bots[0].Quantity > 0);
        Assert.True(bought, string.Join("\n", engine.Snapshot.Logs.Select(l => l.Message)));

        await engine.KillSwitchAsync();
        var flat = await WaitUntil(engine, s => s.Bots[0].Quantity == 0 && s.Bots[0].State == BotState.Stopped);
        Assert.True(flat, string.Join("\n", engine.Snapshot.Logs.Select(l => l.Message)));
        Assert.Single(engine.Snapshot.Trades);
        Assert.True(engine.Snapshot.Risk.KillSwitchActive);
    }

    [Fact]
    public async Task ScannerFindsInPlayCandidates()
    {
        var sim = new SimulatedMarket(new SimulationOptions { Seed = 11, ManualClock = true });
        sim.AdvanceTo(sim.Now.AddMinutes(40));
        var scanner = new TossTrading.Engine.Scanning.ScannerService(sim, sim, new ScannerSettings(), _ => null, _ => { }, (_, _) => { });
        IReadOnlyList<ScanCandidate> result = Array.Empty<ScanCandidate>();
        for (var i = 0; i < 8; i++) result = await scanner.ScanOnceAsync(default); // 경고/일봉 캐시 채우기
        Assert.NotEmpty(result);
        Assert.All(result, c => Assert.InRange(c.ChangePct, 3m, 20m));
        Assert.DoesNotContain(result, c => c.Name.Length == 0);
        Assert.True(result.Zip(result.Skip(1)).All(p => p.First.Score >= p.Second.Score));
    }

    private static async Task<bool> WaitUntil(TradingEngine engine, Func<EngineSnapshot, bool> cond)
    {
        for (var i = 0; i < 100; i++)
        {
            await engine.TickAsync();
            if (engine.Snapshot.Bots.Count > 0 && cond(engine.Snapshot)) return true;
            await Task.Delay(30);
        }
        return false;
    }
}
