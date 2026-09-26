using System.Text;
using TossTrading.Domain;
using TossTrading.Engine;
using TossTrading.Engine.Analytics;
using TossTrading.Engine.Paper;
using TossTrading.Engine.Simulation;
using TossTrading.Toss;

Console.OutputEncoding = Encoding.UTF8;
// --data <폴더> / --source sim|toss 옵션은 위치 인수와 분리해서 읽는다
var dataDir = OptionValue(ref args, "--data");
var sourceOpt = OptionValue(ref args, "--source");
var symbolsOpt = OptionValue(ref args, "--symbols");
var cashOpt = OptionValue(ref args, "--cash");
var cmd = args.FirstOrDefault()?.ToLowerInvariant();

return cmd switch
{
    "check" => await CheckAsync(),
    "sim" => await SimAsync(args.Length > 1 ? int.Parse(args[1]) : 60, args.Length > 2 ? double.Parse(args[2]) : 60,
                            closing: args.Length > 3 && args[3].Equals("closing", StringComparison.OrdinalIgnoreCase), dataDir,
                            auto: args.Length > 3 && args[3].Equals("auto", StringComparison.OrdinalIgnoreCase)),
    "backtest" => await BacktestAsync(args, dataDir, sourceOpt, symbolsOpt, cashOpt),
    "report" => Report(args.Length > 1 ? int.Parse(args[1]) : 30, dataDir, sourceOpt),
    _ => Usage(),
};

static string? OptionValue(ref string[] args, string name)
{
    var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    if (i < 0 || i + 1 >= args.Length) return null;
    var value = args[i + 1];
    args = args.Where((_, k) => k != i && k != i + 1).ToArray();
    return value;
}

static int Usage()
{
    Console.WriteLine("""
        TossTrading CLI

          check              토스 Open API 연결 점검 (Phase 0)
                             환경변수: TOSS_CLIENT_ID, TOSS_CLIENT_SECRET, (선택) TOSS_ACCOUNT_SEQ
          sim [초] [배속] [closing]
                             시뮬레이션 시장 + 모의 체결로 엔진을 헤드리스 실행 (기본 60초, 60배속)
                             closing: 14:40 부터 시작해 종가베팅 봇으로 실행
                             auto: 자동 운용 (단타 자동 선정 → 14:50 정리 → 종가베팅 자동 선정)
                             --data <폴더> 를 주면 로그·분석 기록(journal)을 그 폴더에 저장
          backtest <시작일> <종료일>
                             과거 데이터로 자동 운용(단타→종가매매)을 재생해 거래내역·수익률·수익금 리포트
                             예) backtest 2026-08-01 2026-08-31 --source toss --cash 10000000
                             --source toss (TOSS_CLIENT_ID/SECRET 필요) | synthetic (가상 데이터, 기본)
                             --symbols 005930,000660 (순위 종목 외 추가), 결과: <데이터폴더>\backtests\<실행시각>\
          report [일수]      최근 N일(기본 30) 분석 기록으로 성과 리포트 + 개선 제안 생성
                             --data <폴더> (기본: %LocalAppData%\TossTrading), --source sim|toss
                             결과: <폴더>\reports\report_*.md, trades_*.csv, signals_*.csv
        """);
    return 1;
}

// ---------------------------------------------------------------------------------------------
// Phase 0: 토큰 → 계좌 → 현재가 → 랭킹 → 웹소켓 15초
// ---------------------------------------------------------------------------------------------
static async Task<int> CheckAsync()
{
    var options = new TossOptions
    {
        ClientId = Environment.GetEnvironmentVariable("TOSS_CLIENT_ID") ?? "",
        ClientSecret = Environment.GetEnvironmentVariable("TOSS_CLIENT_SECRET") ?? "",
        AccountSeq = long.TryParse(Environment.GetEnvironmentVariable("TOSS_ACCOUNT_SEQ"), out var seq) ? seq : 0,
    };
    if (!options.HasCredentials)
    {
        Console.WriteLine("TOSS_CLIENT_ID / TOSS_CLIENT_SECRET 환경변수를 설정하세요.");
        return 2;
    }

    await using var conn = new TossConnection(options);
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var ct = cts.Token;

    async Task<bool> Step(string name, Func<Task<string>> f)
    {
        try { Console.WriteLine($"[OK]   {name}: {await f()}"); return true; }
        catch (Exception ex) { Console.WriteLine($"[FAIL] {name}: {ex.Message}"); return false; }
    }

    if (!await Step("토큰 발급", async () => { var t = await conn.Rest.Tokens.GetTokenAsync(ct); return $"길이 {t.Length}"; })) return 3;
    await Step("계좌 목록", async () =>
    {
        var list = await conn.Rest.GetAccountsAsync(ct);
        if (options.AccountSeq == 0 && list.Count > 0) options.AccountSeq = list[0].AccountSeq;
        return string.Join(", ", list.Select(a => $"{a.AccountNo}(seq={a.AccountSeq},{a.AccountType})"));
    });
    await Step("현재가 005930", async () => { var p = await conn.Rest.GetPricesAsync(new[] { "005930" }, ct); return p.Count > 0 ? $"{p[0].LastPrice:N0}" : "없음"; });
    await Step("호가 005930", async () => { var b = await conn.Rest.GetOrderbookAsync("005930", ct); return $"매도1 {b.Asks.FirstOrDefault()?.Price:N0} / 매수1 {b.Bids.FirstOrDefault()?.Price:N0}"; });
    await Step("1분봉 005930", async () => { var c = await conn.Rest.GetCandlesAsync("005930", "1m", 5, null, ct); return $"{c.Candles.Count}개, 최근 {c.Candles.FirstOrDefault()?.Timestamp:HH:mm}"; });
    await Step("거래대금 랭킹", async () =>
    {
        var r = await conn.Rest.GetRankingsAsync("MARKET_TRADING_AMOUNT", "KR", "realtime", 5, true, ct);
        return string.Join(", ", r.Rankings.Select(i => $"{i.Symbol}({i.Price.ChangeRate:P1})"));
    });
    await Step("종목정보", async () => { var s = await conn.Rest.GetStocksAsync(new[] { "005930" }, ct); return s.FirstOrDefault()?.Name ?? "없음"; });
    if (options.AccountSeq > 0)
    {
        await Step("매수가능금액", async () => { var b = await conn.Rest.GetBuyingPowerAsync("KRW", ct); return $"{b.CashBuyingPower:N0}원"; });
        await Step("보유종목", async () => { var h = await conn.Rest.GetHoldingsAsync(ct); return $"{h.Items.Count}종목"; });
        await Step("수수료율", async () => { var c = await conn.Rest.GetCommissionsAsync(ct); return string.Join(", ", c.Select(x => $"{x.MarketCountry} {x.CommissionRate:P3}")); });
        await Step("미체결 주문", async () => { var o = await conn.Rest.ListOrdersAsync("OPEN", ct); return $"{o.Orders.Count}건"; });
    }

    var trades = 0; var books = 0;
    conn.Stream.TradeReceived += _ => Interlocked.Increment(ref trades);
    conn.Stream.OrderBookReceived += _ => Interlocked.Increment(ref books);
    conn.Stream.ConnectionChanged += (c, m) => Console.WriteLine($"       웹소켓: {m}");
    conn.Stream.ErrorReceived += m => Console.WriteLine($"       웹소켓 오류: {m}");
    await conn.Stream.StartAsync(ct);
    await conn.Stream.SetMarketSubscriptionsAsync(new[] { "005930", "000660" }, new[] { "005930" }, ct);
    Console.WriteLine("       웹소켓 15초 수신 대기...");
    await Task.Delay(TimeSpan.FromSeconds(15), ct);
    Console.WriteLine($"[{(trades + books > 0 ? "OK" : "??")}]   웹소켓 수신: 체결 {trades}건, 호가 {books}건 (장 시간이 아니면 0일 수 있음)");
    return 0;
}

// ---------------------------------------------------------------------------------------------
// 헤드리스 시뮬레이션: 스캐너 상위 종목에 봇을 붙이고 자동매매
// ---------------------------------------------------------------------------------------------
static int Report(int days, string? dataDir, string? source)
{
    dataDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TossTrading");
    DataSourceKind? src = source?.ToLowerInvariant() switch
    {
        "sim" or "simulation" => DataSourceKind.Simulation,
        "toss" => DataSourceKind.Toss,
        _ => null,
    };
    var filter = ReportFilter.LastDays(days, Kst.DateOf(Kst.Now)) with { DataSource = src };
    var ds = AnalysisDataSet.Load(Path.Combine(dataDir, "journal"), filter);
    var report = new PerformanceReport(ds, filter);
    var md = report.BuildMarkdown();
    Console.WriteLine(md);

    var outDir = Path.Combine(dataDir, "reports");
    Directory.CreateDirectory(outDir);
    var stamp = $"{Kst.Now:yyyyMMdd_HHmmss}";
    var utf8Bom = new UTF8Encoding(true); // 엑셀에서 한글이 깨지지 않도록 BOM 포함
    File.WriteAllText(Path.Combine(outDir, $"report_{stamp}.md"), md, utf8Bom);
    File.WriteAllText(Path.Combine(outDir, $"trades_{stamp}.csv"), PerformanceReport.TradesCsv(ds.Trades), utf8Bom);
    File.WriteAllText(Path.Combine(outDir, $"signals_{stamp}.csv"), PerformanceReport.SignalsCsv(ds), utf8Bom);
    Console.WriteLine($"저장: {outDir}");
    if (ds.BadLines > 0) Console.WriteLine($"(읽지 못한 줄 {ds.BadLines}개)");
    return 0;
}

static async Task<int> BacktestAsync(string[] args, string? dataDir, string? source, string? symbols, string? cash)
{
    if (args.Length < 3 || !DateOnly.TryParse(args[1], out var from) || !DateOnly.TryParse(args[2], out var to))
    {
        Console.WriteLine("사용법: backtest <시작일 yyyy-MM-dd> <종료일 yyyy-MM-dd> [--source toss|synthetic] [--symbols A,B] [--cash N]");
        return 1;
    }
    dataDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TossTrading");
    var options = new TossTrading.Engine.Backtest.BacktestOptions
    {
        From = from, To = to,
        StartingCash = cash is null ? 10_000_000m : decimal.Parse(cash),
        ExtraSymbols = symbols?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() ?? new(),
        OutputDirectory = Path.Combine(dataDir, "backtests", $"{Kst.Now:yyyyMMdd_HHmmss}"),
    };

    TossConnection? conn = null;
    IHistoryProvider provider;
    if (source?.ToLowerInvariant() == "toss")
    {
        var id = Environment.GetEnvironmentVariable("TOSS_CLIENT_ID");
        var secret = Environment.GetEnvironmentVariable("TOSS_CLIENT_SECRET");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(secret)) { Console.WriteLine("TOSS_CLIENT_ID / TOSS_CLIENT_SECRET 환경변수를 설정하세요."); return 1; }
        conn = new TossConnection(new TossOptions { ClientId = id, ClientSecret = secret });
        provider = new TossTrading.Engine.Backtest.CachedHistoryProvider(new TossHistoryProvider(conn.Source, from), Path.Combine(dataDir, "history"));
    }
    else
    {
        provider = new TossTrading.Engine.Backtest.SyntheticHistoryProvider();
    }

    var lastStage = "";
    var progress = new Progress<TossTrading.Engine.Backtest.BacktestProgress>(p =>
    {
        if (p.Stage != lastStage || p.Message.Contains("완료")) Console.WriteLine($"{p.Overall,5:0.0}% [{p.Stage}] {p.Message}");
        lastStage = p.Stage;
    });
    try
    {
        var result = await new TossTrading.Engine.Backtest.BacktestRunner(provider, options, progress).RunAsync();
        Console.WriteLine();
        Console.WriteLine(TossTrading.Engine.Backtest.BacktestReport.Markdown(result));
        Console.WriteLine($"저장: {TossTrading.Engine.Backtest.BacktestReport.Save(result, options)}");
        return 0;
    }
    finally
    {
        if (conn is not null) await conn.DisposeAsync();
    }
}

static async Task<int> SimAsync(int seconds, double speed, bool closing, string? dataDir, bool auto = false)
{
    var sim = new SimulatedMarket(new SimulationOptions { Speed = speed, Seed = 42, StartTime = closing ? new TimeOnly(14, 40) : new TimeOnly(9, 0) });
    var options = new EngineOptions
    {
        Execution = ExecutionMode.Paper, DataSource = DataSourceKind.Simulation, DataDirectory = dataDir,
        AutoPilot = AutoPilotPlan.Default(enabled: auto),
    };
    options.Scanner.PollSeconds = 2;
    var paper = new PaperBroker(10_000_000m, new CostModel(options.Cost), sim);
    await using var engine = new TradingEngine(options, sim, sim, paper, sim);
    await engine.StartAsync();

    var presets = BotPresets.CreateDefaults();
    var added = new HashSet<string>();
    var end = DateTime.UtcNow.AddSeconds(seconds);
    var lastLog = 0L;
    var tick = 0;
    while (DateTime.UtcNow < end)
    {
        await Task.Delay(1000);
        var s = engine.Snapshot;
        foreach (var c in s.Candidates.Take(auto ? 0 : 4))
        {
            if (added.Count >= 4 || !added.Add(c.Symbol)) continue;
            var preset = (closing ? presets["종가베팅 (익일 매도)"]
                : added.Count % 2 == 0 ? presets["VWAP 눌림 표준"] : presets["ORB 표준"]).Clone();
            preset.Mode = BotMode.FullAuto;
            try
            {
                var id = await engine.AddBotAsync(c.Symbol, c.Name, preset);
                await engine.StartBotAsync(id);
            }
            catch (Exception ex) { Console.WriteLine($"봇 추가 실패: {ex.Message}"); }
        }
        if (auto && ++tick % 15 == 0)
            Console.WriteLine($"   [후보 {s.Time:HH:mm}] " + string.Join(", ", s.Candidates.Take(5).Select(c =>
                c.ClosingTotal > 0 ? $"{c.Name} {c.ClosingPassed}/{c.ClosingTotal}" : $"{c.Name} {c.Score:0}")) + $" | {s.AutoPilot?.Status}");
        foreach (var l in s.Logs.Where(l => l.Seq > lastLog)) Console.WriteLine($"{l.Time:HH:mm:ss} [{l.Level}] {l.Source}: {l.Message}");
        lastLog = s.Logs.Count > 0 ? s.Logs[^1].Seq : lastLog;
    }

    var final = engine.Snapshot;
    if (final.AutoPilot is { } ap) Console.WriteLine($"자동 운용: {ap.Phase} · {ap.Status}");
    Console.WriteLine();
    Console.WriteLine($"== 가상 시각 {final.Time:HH:mm}, 후보 {final.Candidates.Count}개, 봇 {final.Bots.Count}개 ==");
    foreach (var b in final.Bots)
        Console.WriteLine($"  {b.Name}({b.Symbol}) {b.Strategy} {b.StateText} 보유 {b.Quantity} 실현 {b.RealizedNet:N0} 진입 {b.Entries}/{b.MaxEntries}");
    Console.WriteLine($"  거래 {final.Trades.Count}건, 순손익 합계 {final.Trades.Sum(t => t.NetPnl):N0}원, 미실현 {final.Account.UnrealizedNet:N0}원, 평가 {final.Account.Equity:N0}원");
    return 0;
}
