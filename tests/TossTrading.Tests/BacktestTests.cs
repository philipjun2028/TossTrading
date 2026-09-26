using TossTrading.Domain;
using TossTrading.Engine.Backtest;
using TossTrading.Engine.Market;

namespace TossTrading.Tests;

public class ReplayMarketTests
{
    private static readonly DateOnly Day = new(2026, 9, 24);

    private static Bar B(int minute, decimal o, decimal h, decimal l, decimal c, decimal v = 400) => new()
    {
        Start = Kst.At(Day, Kst.MarketOpen).AddMinutes(minute), Open = o, High = h, Low = l, Close = c, Volume = v, Value = c * v,
    };

    private static ReplayMarket Market(params Bar[] bars)
    {
        var m = new ReplayMarket(Kst.At(Day, new TimeOnly(8, 59)));
        var prev = new Bar { Start = Kst.At(Day.AddDays(-1), TimeOnly.MinValue), Open = 9_900, High = 10_100, Low = 9_800, Close = 10_000, Volume = 1_000 };
        m.BeginDay(Day,
            new Dictionary<string, IReadOnlyList<Bar>> { ["A"] = bars, ["X"] = new[] { B(0, 1, 1, 1, 1) } },
            new Dictionary<string, IReadOnlyList<Bar>> { ["A"] = new[] { prev } },
            new Dictionary<string, StockInfo> { ["A"] = new("A", "알파", "KOSPI", "STOCK", true, false, false) });
        return m;
    }

    [Fact]
    public void SplitsBarsIntoConservativePathAndOnlyEmitsSubscribed()
    {
        var m = Market(B(0, 10_000, 10_300, 9_900, 10_200), B(1, 10_200, 10_250, 10_000, 10_050));
        var ticks = new List<TradeTick>();
        m.Trade += ticks.Add;
        m.SetSubscriptionsAsync(new[] { "A" }, Array.Empty<string>(), TestContext.Current.CancellationToken);
        var minute0 = Kst.At(Day, Kst.MarketOpen);
        for (var p = 0; p < 4; p++) m.EmitPhase(minute0, p);
        for (var p = 0; p < 4; p++) m.EmitPhase(minute0.AddMinutes(1), p);

        // 양봉: 시가→저가→고가→종가, 음봉: 시가→고가→저가→종가
        Assert.Equal(new[] { 10_000m, 9_900m, 10_300m, 10_200m, 10_200m, 10_250m, 10_000m, 10_050m }, ticks.Select(t => t.Price));
        Assert.Equal(400m, ticks.Take(4).Sum(t => t.Volume));
        Assert.All(ticks, t => Assert.Equal("A", t.Symbol));
    }

    [Fact]
    public void QueriesNeverSeeTheFuture()
    {
        var m = Market(B(0, 10_000, 10_300, 9_900, 10_200), B(1, 10_200, 10_250, 10_000, 10_050), B(2, 10_050, 10_060, 9_990, 10_000));
        var ct = TestContext.Current.CancellationToken;
        var minute0 = Kst.At(Day, Kst.MarketOpen);

        m.AdvanceTo(minute0.AddSeconds(30));
        m.EmitPhase(minute0, 0);
        Assert.Empty(m.GetTodayMinuteBarsAsync("A", ct).Result);                 // 진행 중인 봉은 아직 없음
        var r = Assert.Single(m.GetRankingsAsync(RankingType.TradingAmount, 100, ct).Result, e => e.Symbol == "A");
        Assert.Equal(10_000m, r.LastPrice);
        Assert.Equal(10_000m, r.BasePrice);                                     // 전일 종가
        Assert.Equal(100m, r.TradingVolume);                                    // 지금까지 체결된 양만

        m.AdvanceTo(minute0.AddMinutes(2));
        Assert.Equal(2, m.GetTodayMinuteBarsAsync("A", ct).Result.Count);      // 끝난 봉 2개만
        Assert.Single(m.GetDailyBarsAsync("A", 20, ct).Result);                 // 전일까지
        var limits = m.GetPriceLimitsAsync("A", ct).Result!;
        Assert.Equal(13_000m, limits.Upper);
        Assert.Equal(7_000m, limits.Lower);
        Assert.Equal("알파", m.GetStocksAsync(new[] { "A" }, ct).Result.Single().Name);
    }

    [Fact]
    public void ContextResetStartsNewSessionFromLastPrice()
    {
        var ctx = new SymbolContext("A", "a");
        ctx.OnTrade(new TradeTick("A", 10_000m, 10, Kst.At(Day, new TimeOnly(9, 0))));
        ctx.OnTrade(new TradeTick("A", 10_500m, 10, Kst.At(Day, new TimeOnly(9, 5))));
        ctx.ResetSession();
        Assert.Equal(10_500m, ctx.PreviousClose);
        Assert.Equal(0m, ctx.DayHigh);
        Assert.Equal(0m, ctx.CumVolume);
        Assert.Empty(ctx.Bars);
        ctx.OnTrade(new TradeTick("A", 10_600m, 10, Kst.At(Day.AddDays(1), new TimeOnly(9, 0))));
        Assert.Equal(10_600m, ctx.DayOpen);
        Assert.Equal(10_600m, ctx.Vwap);
    }
}

public class HistoryProviderTests
{
    [Fact]
    public async Task SyntheticDataIsDeterministicAndConsistent()
    {
        var ct = TestContext.Current.CancellationToken;
        var day = new DateOnly(2026, 8, 5);
        var a = new SyntheticHistoryProvider(seed: 3, symbolCount: 5);
        var b = new SyntheticHistoryProvider(seed: 3, symbolCount: 5);
        var sym = (await a.GetUniverseAsync(ct))[2].Symbol;

        var m1 = await a.GetMinuteBarsAsync(sym, day, ct);
        var m2 = await b.GetMinuteBarsAsync(sym, day, ct);
        Assert.Equal(m1.Select(x => x.Close), m2.Select(x => x.Close));
        Assert.Empty(await a.GetMinuteBarsAsync(sym, new DateOnly(2026, 8, 8), ct)); // 토요일

        var daily = await a.GetDailyBarsAsync(sym, day, 5, ct);
        Assert.Equal(5, daily.Count);
        Assert.Equal(day, Kst.DateOf(daily[^1].Start));
        Assert.Equal(m1.Max(x => x.High), daily[^1].High);                       // 일봉 = 분봉 집계
        Assert.Equal(m1[^1].Close, daily[^1].Close);
    }

    [Fact]
    public async Task CacheServesPastDaysWithoutCallingAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(Path.GetTempPath(), "tt-hist-" + Guid.NewGuid().ToString("N"));
        try
        {
            var today = new DateOnly(2026, 9, 1);
            var inner = new SyntheticHistoryProvider(seed: 1, symbolCount: 3);
            var sym = (await inner.GetUniverseAsync(ct))[0].Symbol;
            var c1 = new CachedHistoryProvider(inner, dir, () => today);
            var first = await c1.GetMinuteBarsAsync(sym, new DateOnly(2026, 8, 5), ct);
            await c1.GetDailyBarsAsync(sym, new DateOnly(2026, 8, 31), 30, ct);
            Assert.Equal(2, c1.Misses);

            var c2 = new CachedHistoryProvider(inner, dir, () => today);
            var again = await c2.GetMinuteBarsAsync(sym, new DateOnly(2026, 8, 5), ct);
            var daily = await c2.GetDailyBarsAsync(sym, new DateOnly(2026, 8, 20), 10, ct);
            Assert.Equal(2, c2.Hits);
            Assert.Equal(0, c2.Misses);
            Assert.Equal(first.Select(x => x.Close), again.Select(x => x.Close));
            Assert.Equal(new DateOnly(2026, 8, 20), Kst.DateOf(daily[^1].Start));   // 캐시에서 기간을 잘라서 준다
            Assert.Equal(10, daily.Count);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

public class BacktestRunnerTests
{
    [Fact]
    public async Task RunsMultipleDaysAndReportsConsistentNumbers()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(Path.GetTempPath(), "tt-bt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new BacktestOptions
            {
                From = new DateOnly(2026, 8, 3), To = new DateOnly(2026, 8, 7),
                MaxSymbolsPerDay = 15, OutputDirectory = dir,
            };
            var progress = new List<BacktestProgress>();
            var result = await new BacktestRunner(new SyntheticHistoryProvider(seed: 7, symbolCount: 40), options,
                new SyncProgress<BacktestProgress>(progress.Add)).RunAsync(ct);

            Assert.False(result.Canceled);
            Assert.Equal(5, result.Days.Count);                                  // 월~금
            Assert.NotEmpty(result.Trades);
            Assert.Equal(result.EndingEquity - result.StartingCash, result.NetProfit);
            Assert.Equal(Math.Round(result.NetProfit / result.StartingCash * 100m, 3), result.ReturnPct);
            Assert.Equal(result.Trades.Count, result.Days.Sum(d => d.Trades));
            Assert.Equal(result.RealizedNet, Math.Round(result.Trades.Sum(t => t.NetPnl), 0));
            Assert.Equal(result.Days[^1].EquityEnd, result.EndingEquity);
            Assert.True(result.MaxDrawdownPct >= 0);
            Assert.All(result.Trades, t => Assert.True(t.ExitTime >= t.EntryTime));
            // 단타는 당일 청산, 종가베팅만 다음 날로 넘어간다
            Assert.All(result.Trades.Where(t => t.Strategy != "종가베팅"), t => Assert.Equal(Kst.DateOf(t.EntryTime), Kst.DateOf(t.ExitTime)));
            Assert.All(result.Trades.Where(t => Kst.DateOf(t.EntryTime) == Kst.DateOf(t.ExitTime)),
                t => Assert.True(Kst.TimeOf(t.ExitTime) <= new TimeOnly(15, 30)));
            Assert.Contains(progress, p => p.Stage == "재생");

            var md = BacktestReport.Markdown(result);
            Assert.Contains("수익률", md);
            Assert.Contains("2026-08-07", md);
            var saved = BacktestReport.Save(result)!;
            Assert.True(File.Exists(Path.Combine(saved, "report.md")));
            Assert.True(File.Exists(Path.Combine(saved, "analysis.md")));
            Assert.Equal(result.Trades.Count + 1, File.ReadAllLines(Path.Combine(saved, "trades.csv")).Length);
            Assert.Equal(6, File.ReadAllLines(Path.Combine(saved, "daily.csv")).Length);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task RejectsInvalidPeriodAndCanBeCanceled()
    {
        var provider = new SyntheticHistoryProvider(seed: 1, symbolCount: 10);
        await Assert.ThrowsAsync<ArgumentException>(() => new BacktestRunner(provider,
            new BacktestOptions { From = new DateOnly(2026, 8, 10), To = new DateOnly(2026, 8, 1) }).RunAsync(TestContext.Current.CancellationToken));

        using var cts = new CancellationTokenSource();
        var runner = new BacktestRunner(provider, new BacktestOptions { From = new DateOnly(2026, 8, 3), To = new DateOnly(2026, 8, 28) },
            new SyncProgress<BacktestProgress>(p => { if (p.Stage == "재생" && p.Done >= 1) cts.Cancel(); }));
        var result = await runner.RunAsync(cts.Token);
        Assert.True(result.Canceled);
        Assert.True(result.Days.Count < 20);
    }
}

/// <summary>Progress&lt;T&gt; 는 동기화 컨텍스트로 보내 순서가 보장되지 않아, 테스트에서는 즉시 호출</summary>
internal sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

public class EtaEstimatorTests
{
    [Fact]
    public void EstimatesFromRecentSpeed()
    {
        var eta = new EtaEstimator(TimeSpan.FromSeconds(30));
        var t0 = new DateTime(2026, 9, 26, 10, 0, 0);
        eta.Reset(t0);
        Assert.Null(eta.Update(t0, 0));
        Assert.Null(eta.Update(t0.AddSeconds(1), 1));                  // 너무 이름
        // 처음엔 느림(일봉 다운로드) → 나중엔 빠름(재생): 최근 속도로 추정
        for (var s = 2; s <= 60; s++) eta.Update(t0.AddSeconds(s), s * 0.5);   // 0.5%/초
        for (var s = 61; s <= 90; s++) eta.Update(t0.AddSeconds(s), 30 + (s - 60) * 2.0); // 2%/초
        var remaining = eta.Update(t0.AddSeconds(91), 92)!.Value;
        Assert.InRange(remaining.TotalSeconds, 3, 6);                  // 8% ÷ 2%/초 ≈ 4초
        Assert.Equal(TimeSpan.FromSeconds(91), eta.Elapsed(t0.AddSeconds(91)));
        Assert.Equal(TimeSpan.Zero, eta.Update(t0.AddSeconds(92), 100));
    }

    [Theory]
    [InlineData(45, "45초")]
    [InlineData(125, "2분 05초")]
    [InlineData(3_900, "1시간 5분")]
    public void FormatsKorean(int seconds, string expected) => Assert.Equal(expected, EtaEstimator.Format(TimeSpan.FromSeconds(seconds)));
}
