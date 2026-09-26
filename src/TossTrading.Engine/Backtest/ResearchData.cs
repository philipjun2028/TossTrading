using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TossTrading.Domain;

namespace TossTrading.Engine.Backtest;

/// <summary>
/// 연구용 데이터 내보내기/읽기.
/// 백테스트 로그에는 "실제로 한 거래"만 남아 다른 진입·청산 규칙을 시험할 수 없다.
/// 과거 데이터 캐시(%LocalAppData%\TossTrading\history)를 작게 묶어 저장소에 올리면
/// 같은 데이터로 여러 설정·전략을 다시 돌려 비교할 수 있다.
///
///   universe.csv            종목코드,종목명,시장,유형,보통주
///   daily.csv.gz            종목코드,일자,시가,고가,저가,종가,거래량,거래대금
///   minute_yyyyMM.csv.gz    종목코드,일자,시각(HHmm),시가,고가,저가,종가,거래량   (정규장 09:00~15:30 만)
/// </summary>
public static class ResearchData
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record ExportSummary(string Directory, int Symbols, int DailyRows, int MinuteFiles, long MinuteRows, long Bytes, DateOnly? From, DateOnly? To);

    /// <summary>캐시 폴더 → 내보내기 폴더. from/to 로 분봉 기간을 제한할 수 있다.</summary>
    public static ExportSummary Export(string cacheDirectory, string outDirectory, DateOnly? from = null, DateOnly? to = null,
        IProgress<BacktestProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outDirectory);
        var dailyDir = Path.Combine(cacheDirectory, "daily");
        var minuteDir = Path.Combine(cacheDirectory, "minute");
        if (!Directory.Exists(dailyDir) && !Directory.Exists(minuteDir))
            throw new DirectoryNotFoundException($"과거 데이터 캐시가 없습니다: {cacheDirectory} (토스 데이터로 백테스트를 한 번 돌려야 생깁니다)");

        // 1) 종목 목록
        var universe = ReadJson<List<StockInfo>>(Path.Combine(cacheDirectory, "universe.json"))?.ToDictionary(u => u.Symbol) ?? new();
        var symbols = new SortedSet<string>(StringComparer.Ordinal);

        // 2) 일봉
        var dailyRows = 0;
        using (var w = GzWriter(Path.Combine(outDirectory, "daily.csv.gz")))
        {
            w.WriteLine("symbol,date,open,high,low,close,volume,value");
            var files = Directory.Exists(dailyDir) ? Directory.GetFiles(dailyDir, "*.json") : Array.Empty<string>();
            for (var i = 0; i < files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i % 100 == 0) progress?.Report(new BacktestProgress("일봉", i, files.Length, $"일봉 {i:N0}/{files.Length:N0}", 30.0 * i / files.Length));
                var sym = Path.GetFileNameWithoutExtension(files[i]);
                using var doc = TryParse(files[i]);
                if (doc is null || !doc.RootElement.TryGetProperty("Bars", out var bars)) continue;
                symbols.Add(sym);
                foreach (var b in bars.EnumerateArray().Select(ToBar))
                {
                    w.WriteLine(string.Join(',', sym, Kst.DateOf(b.Start).ToString("yyyyMMdd", Inv), N(b.Open), N(b.High), N(b.Low), N(b.Close), N(b.Volume), N(Math.Round(b.Value))));
                    dailyRows++;
                }
            }
        }

        // 3) 분봉 (월별 파일)
        long minuteRows = 0;
        var minuteFiles = 0;
        DateOnly? first = null, last = null;
        var dayDirs = Directory.Exists(minuteDir)
            ? Directory.GetDirectories(minuteDir)
                .Select(d => (Dir: d, Ok: DateOnly.TryParseExact(Path.GetFileName(d), "yyyyMMdd", Inv, DateTimeStyles.None, out var dt), Date: dt))
                .Where(x => x.Ok && (from is null || x.Date >= from) && (to is null || x.Date <= to))
                .OrderBy(x => x.Date).ToList()
            : new();
        var open = new TimeOnly(9, 0);
        var close = new TimeOnly(15, 30);
        foreach (var month in dayDirs.GroupBy(x => x.Date.ToString("yyyyMM", Inv)))
        {
            using var w = GzWriter(Path.Combine(outDirectory, $"minute_{month.Key}.csv.gz"));
            w.WriteLine("symbol,date,time,open,high,low,close,volume");
            minuteFiles++;
            foreach (var day in month)
            {
                ct.ThrowIfCancellationRequested();
                var idx = dayDirs.IndexOf(day);
                progress?.Report(new BacktestProgress("분봉", idx, dayDirs.Count, $"분봉 {day.Date:yyyy-MM-dd}", 30 + 70.0 * idx / Math.Max(1, dayDirs.Count)));
                first ??= day.Date;
                last = day.Date;
                foreach (var file in Directory.GetFiles(day.Dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var sym = Path.GetFileNameWithoutExtension(file);
                    using var doc = TryParse(file);
                    if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                    symbols.Add(sym);
                    foreach (var b in doc.RootElement.EnumerateArray().Select(ToBar))
                    {
                        var t = Kst.TimeOf(b.Start);
                        if (t < open || t > close || b.Volume <= 0) continue;
                        w.WriteLine(string.Join(',', sym, day.Date.ToString("yyyyMMdd", Inv), t.ToString("HHmm", Inv),
                            N(b.Open), N(b.High), N(b.Low), N(b.Close), N(b.Volume)));
                        minuteRows++;
                    }
                }
            }
        }

        // 4) 종목 목록 (이름이 없으면 코드로)
        using (var w = new StreamWriter(Path.Combine(outDirectory, "universe.csv"), false, new UTF8Encoding(false)))
        {
            w.WriteLine("symbol,name,market,securityType,isCommonShare");
            foreach (var s in symbols)
            {
                var u = universe.TryGetValue(s, out var info) ? info : new StockInfo(s, s, "", "STOCK", true, false, false);
                w.WriteLine(string.Join(',', s, Csv(u.Name), u.Market, u.SecurityType, u.IsCommonShare ? "1" : "0"));
            }
        }
        File.WriteAllText(Path.Combine(outDirectory, "README.txt"),
            "TossTrading 연구용 과거 데이터 (토스 Open API 캔들). 백테스트: TossTrading.Cli backtest <시작> <종료> --source export:<이 폴더>\n",
            new UTF8Encoding(false));

        var bytes = Directory.GetFiles(outDirectory).Sum(f => new FileInfo(f).Length);
        progress?.Report(new BacktestProgress("완료", 1, 1, "내보내기 완료", 100));
        return new ExportSummary(outDirectory, symbols.Count, dailyRows, minuteFiles, minuteRows, bytes, first, last);
    }

    private static Bar ToBar(JsonElement e) => new()
    {
        Start = e.GetProperty("Start").GetDateTimeOffset(),
        Open = e.GetProperty("Open").GetDecimal(),
        High = e.GetProperty("High").GetDecimal(),
        Low = e.GetProperty("Low").GetDecimal(),
        Close = e.GetProperty("Close").GetDecimal(),
        Volume = e.GetProperty("Volume").GetDecimal(),
        Value = e.TryGetProperty("Value", out var v) ? v.GetDecimal() : 0,
    };

    private static JsonDocument? TryParse(string path)
    {
        try { return JsonDocument.Parse(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private static StreamWriter GzWriter(string path) =>
        new(new GZipStream(File.Create(path), CompressionLevel.SmallestSize), new UTF8Encoding(false));

    private static string N(decimal v) => v.ToString(Inv);
    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}

/// <summary>
/// 내보낸 연구용 데이터로 백테스트 (네트워크 없이). 분봉은 한 달씩 읽어 메모리를 아낀다.
/// </summary>
public sealed class ExportedHistoryProvider : IHistoryProvider
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly string _dir;
    private readonly List<StockInfo> _universe = new();
    private readonly Dictionary<string, List<Bar>> _daily = new();
    private string? _loadedMonth;
    private Dictionary<(string Symbol, DateOnly Date), List<Bar>> _minute = new();
    private readonly object _lock = new();

    public ExportedHistoryProvider(string directory)
    {
        _dir = directory;
        foreach (var line in File.ReadLines(Path.Combine(directory, "universe.csv")).Skip(1))
        {
            var p = SplitCsv(line);
            if (p.Count >= 5) _universe.Add(new StockInfo(p[0], p[1], p[2], p[3], p[4] == "1", false, false));
        }
        foreach (var p in ReadGz(Path.Combine(directory, "daily.csv.gz")))
        {
            if (!_daily.TryGetValue(p[0], out var list)) _daily[p[0]] = list = new();
            var date = DateOnly.ParseExact(p[1], "yyyyMMdd", Inv);
            list.Add(new Bar
            {
                Start = Kst.At(date, TimeOnly.MinValue), Open = D(p[2]), High = D(p[3]), Low = D(p[4]), Close = D(p[5]),
                Volume = D(p[6]), Value = D(p[7]),
            });
        }
        foreach (var list in _daily.Values) list.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    public string Name => $"내보낸 데이터 ({Path.GetFileName(_dir.TrimEnd(Path.DirectorySeparatorChar, '/'))})";

    public Task<IReadOnlyList<StockInfo>> GetUniverseAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<StockInfo>>(_universe);

    public Task<IReadOnlyList<Bar>> GetDailyBarsAsync(string symbol, DateOnly to, int count, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Bar>>(_daily.TryGetValue(symbol, out var l)
            ? l.Where(b => Kst.DateOf(b.Start) <= to).TakeLast(count).Select(b => b.Clone()).ToList()
            : Array.Empty<Bar>());

    public Task<IReadOnlyList<Bar>> GetMinuteBarsAsync(string symbol, DateOnly date, CancellationToken ct)
    {
        lock (_lock)
        {
            var month = date.ToString("yyyyMM", Inv);
            if (_loadedMonth != month)
            {
                _minute = new();
                var path = Path.Combine(_dir, $"minute_{month}.csv.gz");
                if (File.Exists(path))
                {
                    foreach (var p in ReadGz(path))
                    {
                        var d = DateOnly.ParseExact(p[1], "yyyyMMdd", Inv);
                        var t = TimeOnly.ParseExact(p[2], "HHmm", Inv);
                        var key = (p[0], d);
                        if (!_minute.TryGetValue(key, out var list)) _minute[key] = list = new();
                        var v = D(p[7]);
                        var c = D(p[6]);
                        list.Add(new Bar { Start = Kst.At(d, t), Open = D(p[3]), High = D(p[4]), Low = D(p[5]), Close = c, Volume = v, Value = (D(p[4]) + D(p[5]) + c) / 3m * v });
                    }
                }
                _loadedMonth = month;
            }
            return Task.FromResult<IReadOnlyList<Bar>>(_minute.TryGetValue((symbol, date), out var bars)
                ? bars.Select(b => b.Clone()).ToList()
                : Array.Empty<Bar>());
        }
    }

    private static IEnumerable<List<string>> ReadGz(string path)
    {
        using var sr = new StreamReader(new GZipStream(File.OpenRead(path), CompressionMode.Decompress), Encoding.UTF8);
        sr.ReadLine(); // 헤더
        while (sr.ReadLine() is { } line)
            if (line.Length > 0) yield return line.Split(',').ToList();
    }

    private static List<string> SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString());
        return result;
    }

    private static decimal D(string s) => decimal.Parse(s, Inv);
}
