using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TossTrading.Domain;
using TossTrading.Engine.Market;

namespace TossTrading.Engine.Analytics;

/// <summary>
/// 분석용 기록기. journal 폴더의 analysis_yyyyMMdd.jsonl 에 한 줄씩 {"kind":..., "data":...} 형태로 남긴다.
///   kind = trade (거래 분석) | signal (진입 신호) | decision (신호 승인/거절/만료) | followup (이후 가격 추적)
/// 청산·신호 이후 5/15/30/60분 가격과 최고/최저가를 추적해 "손절 후 반등했나", "익절 후 더 올랐나",
/// "진입 안 한 신호는 어땠나"를 알 수 있게 한다. 엔진 이벤트 루프에서만 호출된다.
/// </summary>
public sealed class AnalyticsRecorder
{
    public static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        // 한글을 \uXXXX 로 바꾸지 않아 메모장·엑셀에서도 바로 읽힌다
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private sealed class FollowUp
    {
        public required string RefId { get; init; }
        public required string RefKind { get; init; }
        public required string Symbol { get; init; }
        public required DateTimeOffset Start { get; init; }
        public required decimal StartPrice { get; init; }
        public decimal? EntryPrice { get; init; }
        public decimal? P5, P15, P30, P60, Max30, Min30, Max60, Min60;
    }

    private static readonly TimeOnly SessionEnd = new(15, 30);
    private readonly string? _directory;
    private readonly List<FollowUp> _followUps = new();
    private readonly object _fileLock = new();

    public AnalyticsRecorder(string? directory)
    {
        _directory = directory;
        if (_directory is not null) Directory.CreateDirectory(_directory);
    }

    public IEnumerable<string> FollowUpSymbols => _followUps.Select(f => f.Symbol).Distinct();

    public int ActiveFollowUps => _followUps.Count;

    public void WriteTrade(TradeAnalysisRecord r) => Write("trade", r, r.ExitTime);
    public void WriteSignal(SignalRecord r) => Write("signal", r, r.Time);
    public void WriteDecision(SignalDecisionRecord r) => Write("decision", r, r.Time);

    public void StartFollowUp(string refId, string refKind, string symbol, DateTimeOffset start, decimal startPrice, decimal? entryPrice)
    {
        if (startPrice <= 0) return;
        _followUps.Add(new FollowUp
        {
            RefId = refId, RefKind = refKind, Symbol = symbol, Start = start, StartPrice = startPrice, EntryPrice = entryPrice,
        });
    }

    /// <summary>타이머마다 현재가로 추적 갱신. 60분 경과 또는 장 마감이면 기록하고 종료.</summary>
    public void OnTimer(DateTimeOffset now, Func<string, SymbolContext?> contextOf)
    {
        for (var i = _followUps.Count - 1; i >= 0; i--)
        {
            var f = _followUps[i];
            var ctx = contextOf(f.Symbol);
            var sessionOver = Kst.DateOf(now) != Kst.DateOf(f.Start) || Kst.TimeOf(now) >= SessionEnd;

            if (ctx is { HasData: true } && ctx.LastTradeTime >= f.Start && !sessionOver)
            {
                var p = ctx.LastPrice;
                var elapsed = now - f.Start;
                if (elapsed <= TimeSpan.FromMinutes(30)) { f.Max30 = Max(f.Max30, p); f.Min30 = Min(f.Min30, p); }
                if (elapsed <= TimeSpan.FromMinutes(60)) { f.Max60 = Max(f.Max60, p); f.Min60 = Min(f.Min60, p); }
                if (elapsed >= TimeSpan.FromMinutes(5)) f.P5 ??= p;
                if (elapsed >= TimeSpan.FromMinutes(15)) f.P15 ??= p;
                if (elapsed >= TimeSpan.FromMinutes(30)) f.P30 ??= p;
                if (elapsed >= TimeSpan.FromMinutes(60)) f.P60 ??= p;
            }

            if (sessionOver || now - f.Start >= TimeSpan.FromMinutes(60))
            {
                var close = sessionOver && ctx is { HasData: true } && Kst.DateOf(ctx.LastTradeTime) == Kst.DateOf(f.Start) ? ctx.LastPrice : (decimal?)null;
                Complete(f, now, close, complete: true);
                _followUps.RemoveAt(i);
            }
        }
    }

    /// <summary>엔진 종료 시 진행 중인 추적을 "미완료"로 기록</summary>
    public void FlushIncomplete(DateTimeOffset now)
    {
        foreach (var f in _followUps) Complete(f, now, null, complete: false);
        _followUps.Clear();
    }

    private void Complete(FollowUp f, DateTimeOffset now, decimal? sessionClose, bool complete) =>
        Write("followup", new FollowUpRecord(f.RefId, f.RefKind, f.Symbol, f.Start, f.StartPrice, f.EntryPrice,
            f.P5, f.P15, f.P30, f.P60, f.Max30, f.Min30, f.Max60, f.Min60, sessionClose, complete), f.Start);

    private static decimal? Max(decimal? a, decimal b) => a is { } x ? Math.Max(x, b) : b;
    private static decimal? Min(decimal? a, decimal b) => a is { } x ? Math.Min(x, b) : b;

    private void Write<T>(string kind, T data, DateTimeOffset time)
    {
        if (_directory is null) return;
        try
        {
            var line = JsonSerializer.Serialize(new { kind, data }, Json);
            var path = Path.Combine(_directory, $"analysis_{Kst.DateOf(time):yyyyMMdd}.jsonl");
            lock (_fileLock) File.AppendAllText(path, line + Environment.NewLine, Utf8NoBom);
        }
        catch
        {
            // 기록 실패가 매매를 멈추지 않도록
        }
    }
}
