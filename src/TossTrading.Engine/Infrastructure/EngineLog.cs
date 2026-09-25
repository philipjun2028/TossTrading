using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TossTrading.Engine.Infrastructure;

/// <summary>메모리 링버퍼 + 날짜별 파일 로그. 스레드 안전.</summary>
public sealed class EngineLog : IAsyncDisposable
{
    private const int Capacity = 800;
    private readonly ConcurrentQueue<LogEntry> _ring = new();
    private readonly Channel<LogEntry> _fileQueue = Channel.CreateUnbounded<LogEntry>();
    private readonly Func<DateTimeOffset> _now;
    private readonly string? _directory;
    private readonly Task _writer;
    private long _seq;

    public EngineLog(string? directory, Func<DateTimeOffset> now)
    {
        _directory = directory;
        _now = now;
        if (_directory is not null) Directory.CreateDirectory(_directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public event Action<LogEntry>? Written;

    public void Write(LogLevel level, string source, string message)
    {
        var e = new LogEntry(Interlocked.Increment(ref _seq), _now(), level, source, message);
        _ring.Enqueue(e);
        while (_ring.Count > Capacity) _ring.TryDequeue(out _);
        _fileQueue.Writer.TryWrite(e);
        Written?.Invoke(e);
    }

    public IReadOnlyList<LogEntry> Recent(int max = 300)
    {
        var all = _ring.ToArray();
        return all.Length <= max ? all : all[^max..];
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var e in _fileQueue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_directory is null) continue;
            try
            {
                var path = Path.Combine(_directory, $"log_{e.Time:yyyyMMdd}.txt");
                await File.AppendAllTextAsync(path, $"{e.Time:HH:mm:ss.fff} [{e.Level}] {e.Source}: {e.Message}{Environment.NewLine}", Encoding.UTF8).ConfigureAwait(false);
            }
            catch
            {
                // 로그 파일 실패는 매매에 영향을 주지 않는다
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _fileQueue.Writer.TryComplete();
        try { await _writer.ConfigureAwait(false); } catch { }
    }
}

/// <summary>거래 기록 (JSON Lines, 날짜별 파일). 리포트/검증(설계 문서 10장)의 원천 데이터.</summary>
public sealed class TradeJournal
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string? _directory;
    private readonly object _lock = new();

    public TradeJournal(string? directory)
    {
        _directory = directory;
        if (_directory is not null) Directory.CreateDirectory(_directory);
    }

    public void Append(ClosedTrade trade)
    {
        if (_directory is null) return;
        try
        {
            var path = Path.Combine(_directory, $"trades_{trade.ExitTime:yyyyMMdd}.jsonl");
            var line = JsonSerializer.Serialize(trade, Json);
            lock (_lock) File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // 기록 실패는 매매를 멈추지 않는다
        }
    }
}

/// <summary>
/// 실시간 체결/호가 원시 기록 (리플레이 백테스트용, 설계 문서 8.9).
/// 토스는 과거 틱을 제공하지 않으므로 첫날부터 쌓는다. 날짜별 gzip JSON Lines.
/// </summary>
public sealed class TickRecorder : IAsyncDisposable
{
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(100_000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly string _directory;
    private readonly Task _writer;

    public TickRecorder(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public void Record(Domain.TradeTick t) =>
        _queue.Writer.TryWrite(FormattableString.Invariant($"{{\"k\":\"t\",\"s\":\"{t.Symbol}\",\"p\":{t.Price},\"v\":{t.Volume},\"ts\":\"{t.Timestamp:O}\"}}"));

    public void Record(Domain.OrderBookSnapshot b)
    {
        var asks = string.Join(",", b.Asks.Take(5).Select(l => FormattableString.Invariant($"[{l.Price},{l.Volume}]")));
        var bids = string.Join(",", b.Bids.Take(5).Select(l => FormattableString.Invariant($"[{l.Price},{l.Volume}]")));
        _queue.Writer.TryWrite(FormattableString.Invariant($"{{\"k\":\"b\",\"s\":\"{b.Symbol}\",\"ts\":\"{b.Timestamp:O}\",\"a\":[{asks}],\"b\":[{bids}]}}"));
    }

    private async Task WriteLoopAsync()
    {
        string? currentPath = null;
        Stream? stream = null;
        StreamWriter? writer = null;
        try
        {
            await foreach (var line in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var path = Path.Combine(_directory, $"ticks_{DateTime.Now:yyyyMMdd}_{Environment.ProcessId}.jsonl.gz");
                if (path != currentPath)
                {
                    if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
                    stream = new System.IO.Compression.GZipStream(File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read), System.IO.Compression.CompressionLevel.Fastest);
                    writer = new StreamWriter(stream, Encoding.UTF8);
                    currentPath = path;
                }
                await writer!.WriteLineAsync(line).ConfigureAwait(false);
            }
        }
        catch
        {
            // 기록 실패는 무시
        }
        finally
        {
            if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        try { await _writer.ConfigureAwait(false); } catch { }
    }
}
