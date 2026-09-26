using TossTrading.Domain;
using TossTrading.Engine.Analytics;
using TossTrading.Engine.Infrastructure;
using TossTrading.Engine.Market;
using TossTrading.Engine.Strategies;
using TossTrading.Engine.Trading;

namespace TossTrading.Tests;

public class TradeAnalyticsTests
{
    private readonly FakeHost _host = new();
    private readonly SymbolContext _ctx = new("000001", "테스트");

    private void Price(decimal p)
    {
        _ctx.OnOrderBook(new OrderBookSnapshot("000001", _host.Now,
            new[] { new PriceLevel(TickRules.AddTicks(p, 1), 100) }, new[] { new PriceLevel(p, 100) }));
        _ctx.OnTrade(new TradeTick("000001", p, 10, _host.Now));
    }

    private void Tick(TradingBot bot, decimal p)
    {
        _host.Clock.Now = _host.Clock.Now.AddSeconds(5);
        Price(p);
        bot.OnMarket(SignalTrigger.Trade);
    }

    private TradingBot BoughtBot(BotSettings s)
    {
        Price(10_000m);
        var bot = new TradingBot("b1", _ctx, s, _host);
        bot.Start();
        Assert.True(bot.ManualBuy());
        bot.OnFill(_host.Orders[0].Id, OrderSide.Buy, 100, 10_020m);
        bot.OnOrderDone(_host.Orders[0].Id, OrderStatus.Filled, null);
        return bot;
    }

    [Fact]
    public void StopLossTradeRecordsExcursionSlippageAndContext()
    {
        var bot = BoughtBot(new BotSettings { StopLossPct = 1.5m, UseStructuralStop = false });
        Tick(bot, 10_150m);                 // 최고 +1.3%
        Tick(bot, 9_900m);
        Tick(bot, 9_860m);                  // 손절가 9,860 (10,010 × 0.985 내림) 도달
        var exit = _host.Orders[1];
        Assert.Contains("손절", exit.Reason);
        bot.OnFill(exit.Id, OrderSide.Sell, 100, 9_850m);

        var a = Assert.Single(_host.Analyses);
        Assert.Equal(ExitKind.StopLoss, a.FinalExitKind);
        Assert.Equal(10_020m, a.AverageEntry);
        Assert.Equal(10_010m, a.SignalPrice);                          // 결정 시점 매도1호가
        Assert.Equal(Math.Round((10_020m / 10_010m - 1m) * 100m, 3), a.EntrySlippagePct);
        Assert.Equal(Math.Round((10_150m / 10_020m - 1m) * 100m, 3), a.MfePct);
        Assert.Equal(Math.Round((9_860m / 10_020m - 1m) * 100m, 3), a.MaePct);
        var leg = Assert.Single(a.Exits);
        Assert.Equal(9_860m, leg.TriggerPrice);
        Assert.Equal(Math.Round((9_850m / 9_860m - 1m) * 100m, 3), a.ExitSlippagePct);
        Assert.NotNull(a.EntryContext);
        Assert.Equal(30, a.EntryContext!.MinutesFromOpen);                  // 09:30 진입
        Assert.True(a.NetPnl < 0);
        Assert.Equal(a.GrossPnl - a.NetPnl, a.Costs);
        Assert.Equal(1.5m, a.Settings.StopLossPct);
        Assert.False(a.Overnight);
    }

    [Fact]
    public void PartialThenTrailingRecordsBothLegs()
    {
        var bot = BoughtBot(new BotSettings { TakeProfitPct = 0, BotTargetProfitPct = 0 });
        Tick(bot, 10_230m);                                                // +2% 이상 → 1차 분할 익절
        bot.OnFill(_host.Orders[1].Id, OrderSide.Sell, 50, 10_220m);
        bot.OnOrderDone(_host.Orders[1].Id, OrderStatus.Filled, null);
        Tick(bot, 10_600m);
        Tick(bot, 10_460m);                                                // 트레일링
        bot.OnFill(_host.Orders[2].Id, OrderSide.Sell, 50, 10_450m);

        var a = Assert.Single(_host.Analyses);
        Assert.Equal(2, a.Exits.Count);
        Assert.Equal(ExitKind.PartialTakeProfit, a.Exits[0].Kind);
        Assert.Equal(ExitKind.Trailing, a.Exits[1].Kind);
        Assert.Equal(ExitKind.Trailing, a.FinalExitKind);
        Assert.Equal(100m, a.Quantity);
        Assert.True(a.MfePct > 5m);
    }

    [Fact]
    public void TrackSurvivesCaptureAndRestore()
    {
        var bot = BoughtBot(new BotSettings());
        Tick(bot, 10_300m);
        var dir = Path.Combine(Path.GetTempPath(), "tt-an-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new StateStore(dir, "paper");
            store.Save(new[] { bot.Capture() }, null);
            var loaded = store.LoadBots().Single();
            Assert.NotNull(loaded.Track);
            Assert.Equal(10_300m, loaded.Track!.MaxPrice);

            var restored = TradingBot.Restore(loaded, _ctx, _host);
            Tick(restored, 9_000m);                                        // 손절
            restored.OnFill(_host.Orders[^1].Id, OrderSide.Sell, 100, 9_000m);
            var a = Assert.Single(_host.Analyses);
            Assert.Equal(Math.Round((10_300m / 10_020m - 1m) * 100m, 3), a.MfePct);
            Assert.NotNull(a.EntryContext);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void BlockedAndPendingSignalsAreRecorded()
    {
        var day = new DateOnly(2026, 9, 25);
        var s = BotPresets.CreateDefaults()["종가베팅 (익일 매도)"];
        s.Mode = BotMode.FullAuto;
        var ctx = StrongCloser(day, new TimeOnly(15, 5));
        _host.Clock.Now = Kst.At(day, new TimeOnly(15, 5));
        _host.AllowEntry = false;
        var bot = new TradingBot("b1", ctx, s, _host);
        bot.Start();
        bot.OnMarket(SignalTrigger.Trade);

        var sig = Assert.Single(_host.Signals);
        Assert.Equal("차단", sig.Decision);
        Assert.Contains("리스크", sig.DecisionDetail);
        Assert.Equal("종가베팅", sig.Strategy);
        Assert.NotNull(sig.Context.ChangePct);

        // 반자동: 승인 대기 → 60초 경과 만료
        var host2 = new FakeHost();
        host2.Clock.Now = Kst.At(day, new TimeOnly(15, 5));
        s.Mode = BotMode.SemiAuto;
        var bot2 = new TradingBot("b2", StrongCloser(day, new TimeOnly(15, 5)), s, host2);
        bot2.Start();
        bot2.OnMarket(SignalTrigger.Trade);
        Assert.Equal("승인대기", Assert.Single(host2.Signals).Decision);
        host2.Clock.Now = host2.Clock.Now.AddSeconds(61);
        bot2.OnTimer();
        var d = Assert.Single(host2.Decisions);
        Assert.Equal(host2.Signals[0].SignalId, d.SignalId);
        Assert.Equal("만료", d.Decision);
    }

    private static SymbolContext StrongCloser(DateOnly day, TimeOnly until)
    {
        var ctx = new SymbolContext("000001", "테스트") { PreviousClose = 10_000m };
        ctx.OnTrade(new TradeTick("000001", 10_000m, 1, Kst.At(day, new TimeOnly(9, 0))));
        var t = Kst.At(day, new TimeOnly(9, 0, 30));
        var end = Kst.At(day, until);
        var price = 10_300m;
        while (t < end)
        {
            price = Math.Min(10_800m, price + 10m);
            ctx.OnTrade(new TradeTick("000001", price, 100, t));
            t = t.AddMinutes(1);
        }
        return ctx;
    }
}

public class AnalyticsRecorderTests
{
    private static readonly DateOnly Day = new(2026, 9, 25);

    [Fact]
    public void FollowUpTracksPricesAfterExit()
    {
        var rec = new AnalyticsRecorder(null);
        var ctx = new SymbolContext("A", "a");
        var start = Kst.At(Day, new TimeOnly(10, 0));
        rec.StartFollowUp("t1", "exit", "A", start, 10_000m, 10_100m);
        Assert.Equal(new[] { "A" }, rec.FollowUpSymbols);

        for (var m = 1; m <= 61; m++)
        {
            var now = start.AddMinutes(m);
            ctx.OnTrade(new TradeTick("A", 10_000m + m * 10m, 1, now));
            rec.OnTimer(now, s => s == "A" ? ctx : null);
        }
        Assert.Equal(0, rec.ActiveFollowUps);
    }

    [Fact]
    public void RecordsRoundTripThroughJournalAndReport()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tt-rep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rec = new AnalyticsRecorder(dir);
            var ctx = new SymbolContext("A", "알파") { PreviousClose = 9_000m };
            var t0 = Kst.At(Day, new TimeOnly(9, 40));
            ctx.OnTrade(new TradeTick("A", 10_000m, 100, t0));

            // 손절 6건: 매수가 10,000 → 9,850, 이후 60분 안에 매수가 회복 → "손절이 타이트" 제안
            for (var i = 0; i < 6; i++)
            {
                var exitAt = t0.AddMinutes(i * 5 + 2);
                var trade = Trade($"T{i}", exitAt, entry: 10_000m, exit: 9_850m, ExitKind.StopLoss, mfe: 0.2m, mae: -1.5m);
                rec.WriteTrade(trade);
                rec.StartFollowUp(trade.TradeId, "exit", "A", exitAt, 9_850m, 10_000m);
            }
            for (var m = 1; m <= 120; m++)
            {
                var now = t0.AddMinutes(m);
                ctx.OnTrade(new TradeTick("A", m < 20 ? 9_800m : 10_200m, 10, now));
                rec.OnTimer(now, s => s == "A" ? ctx : null);
            }
            Assert.Equal(0, rec.ActiveFollowUps);

            var filter = new ReportFilter(Day, Day);
            var ds = AnalysisDataSet.Load(dir, filter);
            Assert.Equal(6, ds.Trades.Count);
            Assert.Equal(6, ds.FollowUps.Count);
            Assert.All(ds.FollowUps.Values, f => Assert.Equal(10_200m, f.Max60m));
            Assert.Equal("알파", ds.Trades[0].Name);

            var report = new PerformanceReport(ds, filter);
            var md = report.BuildMarkdown();
            Assert.Contains("매수가를 회복한 경우 6건", md);
            Assert.Contains(report.Suggestions, s => s.Contains("손절이 너무 타이트"));
            Assert.Contains("알파", PerformanceReport.TradesCsv(ds.Trades));

            // 한글이 이스케이프되지 않고 저장되어 사람이 읽을 수 있어야 한다
            var raw = File.ReadAllText(Directory.GetFiles(dir, "analysis_*.jsonl").Single());
            Assert.Contains("알파", raw);

            // 필터: 토스 데이터만 → 없음
            var tossOnly = AnalysisDataSet.Load(dir, filter with { DataSource = DataSourceKind.Toss });
            Assert.Empty(tossOnly.Trades);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void BucketsAndTimeBuckets()
    {
        Assert.Equal("a. 3% 미만", PerformanceReport.Bucket(1m, new[] { 3m, 6m }, "%"));
        Assert.Equal("b. 3~6%", PerformanceReport.Bucket(3m, new[] { 3m, 6m }, "%"));
        Assert.Equal("c. 6% 이상", PerformanceReport.Bucket(9m, new[] { 3m, 6m }, "%"));
        Assert.Equal("(없음)", PerformanceReport.Bucket(null, new[] { 3m }, "%"));
        Assert.Equal("09:30~10:00", PerformanceReport.TimeBucket(Kst.At(Day, new TimeOnly(9, 47))));
    }

    [Fact]
    public void EmptyReportDoesNotThrow()
    {
        var filter = new ReportFilter(Day, Day);
        var md = new PerformanceReport(new AnalysisDataSet(), filter).BuildMarkdown();
        Assert.Contains("분석할 거래가 없습니다", md);
    }

    private static TradeAnalysisRecord Trade(string id, DateTimeOffset exitAt, decimal entry, decimal exit, ExitKind kind, decimal mfe, decimal mae)
    {
        var qty = 100m;
        var cost = new CostModel(new CostSettings());
        var net = cost.NetPnl(entry, exit, qty);
        var gross = (exit - entry) * qty;
        var entryAt = exitAt.AddMinutes(-2);
        var ctx = new MarketSnapshot(entryAt, entry, 11m, entry, 0.5m, 0.8m, entry, entry * 0.9m, 120m, 0.8m, 2m, 1, entry - 10m, entry, 40);
        return new TradeAnalysisRecord(id, "b1", "A", "알파", "ORB", BotMode.FullAuto, ExecutionMode.Paper, DataSourceKind.Simulation,
            entryAt, entry, entry + 10m, entryAt, entry, qty, entry * 0.985m, 1.5m, "ORB 돌파", ctx,
            exitAt, exit, new[] { new ExitLeg(exitAt, qty, exit, exit, kind, "손절") }, kind,
            gross, gross - net, net, net / (entry * qty) * 100m, net / (entry * 0.015m * qty), mfe, mae, 0m, 0m, 2, false,
            new BotSettings(), null);
    }
}
