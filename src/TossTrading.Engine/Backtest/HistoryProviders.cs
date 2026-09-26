using System.Text.Json;
using TossTrading.Domain;

namespace TossTrading.Engine.Backtest;

/// <summary>
/// 가상 과거 데이터 (API 키 없이 백테스트 연습 / 테스트용). 같은 시드면 항상 같은 데이터.
/// 평일만 거래일, 종목마다 가끔 "재료 뜬 날"(갭상승 + 거래량 급증 + 추세)이 섞인다.
/// </summary>
public sealed class SyntheticHistoryProvider : IHistoryProvider
{
    private static readonly DateOnly Epoch = new(2025, 1, 1);
    private readonly int _seed;
    private readonly List<StockInfo> _universe;
    private readonly Dictionary<string, List<(DateOnly Date, decimal Open, decimal Close, decimal Volume, bool InPlay, double Drift)>> _walks = new();
    private readonly object _lock = new();

    public SyntheticHistoryProvider(int seed = 7, int symbolCount = 60)
    {
        _seed = seed;
        string[] a = { "가온", "나래", "다온", "라온", "마루", "바름", "새봄", "아라", "한결", "하람" };
        string[] b = { "전자", "바이오", "소재", "로보틱스", "에너지", "제약" };
        _universe = Enumerable.Range(0, symbolCount)
            .Select(i => new StockInfo($"8{i:00000}", a[i % a.Length] + b[i / a.Length % b.Length] + (i >= 60 ? i.ToString() : ""), "KOSDAQ", "STOCK", true, false, false))
            .ToList();
    }

    public string Name => "가상 데이터";

    public Task<IReadOnlyList<StockInfo>> GetUniverseAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<StockInfo>>(_universe);

    public Task<IReadOnlyList<Bar>> GetDailyBarsAsync(string symbol, DateOnly to, int count, CancellationToken ct)
    {
        var walk = Walk(symbol, to);
        var bars = walk.Where(d => d.Date <= to).TakeLast(count).Select(d =>
        {
            var minute = MinutePath(symbol, d);
            return new Bar
            {
                Start = Kst.At(d.Date, TimeOnly.MinValue), Open = d.Open, Close = d.Close,
                High = minute.Max(m => m.High), Low = minute.Min(m => m.Low),
                Volume = minute.Sum(m => m.Volume), Value = minute.Sum(m => m.Value),
            };
        }).ToList();
        return Task.FromResult<IReadOnlyList<Bar>>(bars);
    }

    public Task<IReadOnlyList<Bar>> GetMinuteBarsAsync(string symbol, DateOnly date, CancellationToken ct)
    {
        var day = Walk(symbol, date).FirstOrDefault(d => d.Date == date);
        return Task.FromResult<IReadOnlyList<Bar>>(day.Date == date ? MinutePath(symbol, day) : Array.Empty<Bar>());
    }

    private List<(DateOnly Date, decimal Open, decimal Close, decimal Volume, bool InPlay, double Drift)> Walk(string symbol, DateOnly to)
    {
        lock (_lock)
        {
            if (!_walks.TryGetValue(symbol, out var list))
            {
                list = new();
                _walks[symbol] = list;
            }
            if (list.Count > 0 && list[^1].Date >= to) return list;

            // 항상 처음(Epoch)부터 굴려야 같은 시드에서 같은 결과가 나온다
            list.Clear();
            var rng = new Random(StableSeed(_seed, symbol, 0));
            var idx = int.Parse(symbol[1..]);
            var close = TickRules.RoundDown(2_000m + idx * 1_370m % 60_000m);
            var date = Epoch;
            var baseVol = 200_000m + idx * 17_000m % 900_000m;
            for (; date <= to; date = date.AddDays(1))
            {
                if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                var inPlay = rng.NextDouble() < 0.07;
                var gap = inPlay ? 0.02 + rng.NextDouble() * 0.06 : Normal(rng) * 0.008;
                // 재료 뜬 날의 상승을 평소의 완만한 되돌림이 상쇄 → 장기적으로 가격대가 크게 변하지 않게
                var drift = inPlay ? -0.02 + rng.NextDouble() * 0.14 : -0.004 + Normal(rng) * 0.015;
                if (close > 150_000m) drift -= 0.01;
                if (close < 2_000m) drift += 0.01;
                var open = Math.Max(1_000m, TickRules.RoundDown(close * (decimal)(1 + gap)));
                var dayClose = Math.Max(1_000m, TickRules.RoundDown(open * (decimal)(1 + drift)));
                var upper = TickRules.RoundDown(close * 1.3m);
                var lower = TickRules.RoundUp(close * 0.7m);
                open = Math.Clamp(open, lower, upper);
                dayClose = Math.Clamp(dayClose, lower, upper);
                var vol = baseVol * (decimal)(inPlay ? 4 + rng.NextDouble() * 6 : 0.6 + rng.NextDouble() * 0.8);
                list.Add((date, open, dayClose, Math.Round(vol), inPlay, drift));
                close = dayClose;
            }
            return list;
        }
    }

    /// <summary>시가→종가를 잇는 1분봉 경로 (Brownian bridge + 장 초반/막판 거래량 집중)</summary>
    private List<Bar> MinutePath(string symbol, (DateOnly Date, decimal Open, decimal Close, decimal Volume, bool InPlay, double Drift) d)
    {
        const int n = 380; // 09:00~15:19
        var rng = new Random(StableSeed(_seed, symbol, d.Date.DayNumber));
        var noise = new double[n + 1];
        var sigma = (d.InPlay ? 0.0035 : 0.0018);
        for (var i = 1; i <= n; i++) noise[i] = noise[i - 1] + Normal(rng) * sigma;
        var o = (double)d.Open;
        var c = (double)d.Close;
        var path = new double[n + 1];
        for (var i = 0; i <= n; i++)
        {
            var bridge = noise[i] - noise[n] * i / n;
            path[i] = (o + (c - o) * i / n) * (1 + bridge);
        }
        var weights = Enumerable.Range(0, n).Select(i => 1 + 2.5 * Math.Exp(-i / 25.0) + 0.8 * Math.Exp(-(n - i) / 15.0)).ToArray();
        var wsum = weights.Sum();
        var bars = new List<Bar>(n + 1);
        var start = Kst.At(d.Date, Kst.MarketOpen);
        for (var i = 0; i < n; i++)
        {
            var p0 = (decimal)path[i];
            var p1 = (decimal)path[i + 1];
            var wig = (decimal)(Math.Abs(Normal(rng)) * sigma * 0.6);
            var open = TickRules.RoundDown(p0);
            var close = TickRules.RoundDown(p1);
            var high = TickRules.RoundUp(Math.Max(p0, p1) * (1 + wig));
            var low = TickRules.RoundDown(Math.Min(p0, p1) * (1 - wig));
            var vol = Math.Max(1m, Math.Round(d.Volume * 0.97m * (decimal)(weights[i] / wsum)));
            bars.Add(new Bar { Start = start.AddMinutes(i), Open = open, High = Math.Max(high, Math.Max(open, close)), Low = Math.Min(low, Math.Min(open, close)), Close = close, Volume = vol, Value = (open + close) / 2 * vol });
        }
        // 15:30 종가 단일가
        var last = TickRules.RoundDown(d.Close);
        var auction = Math.Round(d.Volume * 0.03m);
        bars.Add(new Bar { Start = start.AddMinutes(390), Open = last, High = last, Low = last, Close = last, Volume = Math.Max(1, auction), Value = last * Math.Max(1, auction) });
        return bars;
    }

    /// <summary>실행할 때마다 같은 값 (System.HashCode 는 프로세스마다 달라 재현이 안 됨)</summary>
    private static int StableSeed(int seed, string symbol, int salt)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var ch in $"{seed}|{symbol}|{salt}") h = (h ^ ch) * 16777619u;
            return (int)(h & 0x7FFFFFFF);
        }
    }

    private static double Normal(Random r)
    {
        var u1 = 1.0 - r.NextDouble();
        var u2 = r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}

/// <summary>
/// 과거 데이터 디스크 캐시. 지난 거래일 데이터는 바뀌지 않으므로 한 번 받으면 다시 조회하지 않는다.
///   {dir}/daily/{종목}.json, {dir}/minute/{yyyyMMdd}/{종목}.json
/// </summary>
public sealed class CachedHistoryProvider : IHistoryProvider
{
    private sealed record DailyCache(DateOnly To, int Count, List<Bar> Bars);

    private readonly IHistoryProvider _inner;
    private readonly string _dir;
    private readonly Func<DateOnly> _today;

    public CachedHistoryProvider(IHistoryProvider inner, string directory, Func<DateOnly>? today = null)
    {
        _inner = inner;
        _dir = directory;
        _today = today ?? (() => Kst.DateOf(Kst.Now));
    }

    public string Name => _inner.Name;
    public int Hits { get; private set; }
    public int Misses { get; private set; }

    /// <summary>종목 목록은 매번 새로 받되(상장·폐지 반영) 이름을 연구용 내보내기에 쓰도록 저장해 둔다</summary>
    public async Task<IReadOnlyList<StockInfo>> GetUniverseAsync(CancellationToken ct)
    {
        var path = Path.Combine(_dir, "universe.json");
        try
        {
            var list = await _inner.GetUniverseAsync(ct).ConfigureAwait(false);
            if (list.Count > 0)
            {
                var merged = (Read<List<StockInfo>>(path) ?? new()).ToDictionary(u => u.Symbol);
                foreach (var u in list) merged[u.Symbol] = u;
                Write(path, merged.Values.OrderBy(u => u.Symbol, StringComparer.Ordinal).ToList());
            }
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Read<List<StockInfo>>(path) is { Count: > 0 })
        {
            return Read<List<StockInfo>>(path)!; // 네트워크 실패 시 저장된 목록
        }
    }

    public async Task<IReadOnlyList<Bar>> GetDailyBarsAsync(string symbol, DateOnly to, int count, CancellationToken ct)
    {
        var path = Path.Combine(_dir, "daily", Safe(symbol) + ".json");
        if (Read<DailyCache>(path) is { } c && c.To >= to && c.Count >= count && to < _today())
        {
            Hits++;
            return c.Bars.Where(b => Kst.DateOf(b.Start) <= to).TakeLast(count).ToList();
        }
        Misses++;
        var bars = await _inner.GetDailyBarsAsync(symbol, to, count, ct).ConfigureAwait(false);
        var complete = bars.Where(b => Kst.DateOf(b.Start) < _today()).ToList(); // 오늘 봉은 아직 바뀔 수 있다
        if (complete.Count > 0) Write(path, new DailyCache(Min(to, _today().AddDays(-1)), count, complete));
        return bars;
    }

    public async Task<IReadOnlyList<Bar>> GetMinuteBarsAsync(string symbol, DateOnly date, CancellationToken ct)
    {
        var path = Path.Combine(_dir, "minute", date.ToString("yyyyMMdd"), Safe(symbol) + ".json");
        if (date < _today() && Read<List<Bar>>(path) is { } cached)
        {
            Hits++;
            return cached;
        }
        Misses++;
        var bars = await _inner.GetMinuteBarsAsync(symbol, date, ct).ConfigureAwait(false);
        if (date < _today()) Write(path, bars.ToList());
        return bars;
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
    private static string Safe(string s) => string.Concat(s.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));

    private static T? Read<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }
        catch (IOException) { /* 캐시 실패는 무시 */ }
    }
}
