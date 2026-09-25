using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TossTrading.Engine.Paper;
using TossTrading.Engine.Trading;

namespace TossTrading.Engine.Infrastructure;

/// <summary>
/// 봇·모의계좌 상태를 JSON 파일로 저장/복원 (종가매매처럼 장 마감 후에도 포지션을 들고 가는 경우 필수).
/// 쓰기는 임시 파일 → 교체 방식이라 저장 중 종료돼도 기존 파일이 깨지지 않는다.
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _botsPath;
    private readonly string _paperPath;
    private string? _lastBots;
    private string? _lastPaper;

    public StateStore(string directory, string modeKey)
    {
        Directory.CreateDirectory(directory);
        _botsPath = Path.Combine(directory, $"bots_{modeKey}.json");
        _paperPath = Path.Combine(directory, "paper_account.json");
    }

    public IReadOnlyList<BotPersistState> LoadBots() =>
        Read<List<BotPersistState>>(_botsPath) ?? new List<BotPersistState>();

    public PaperBroker.PaperAccountState? LoadPaper() => Read<PaperBroker.PaperAccountState>(_paperPath);

    /// <summary>내용이 바뀐 경우에만 기록한다.</summary>
    public void Save(IReadOnlyList<BotPersistState> bots, PaperBroker.PaperAccountState? paper)
    {
        var botsJson = JsonSerializer.Serialize(bots, Json);
        if (botsJson != _lastBots && Write(_botsPath, botsJson)) _lastBots = botsJson;
        if (paper is null) return;
        var paperJson = JsonSerializer.Serialize(paper, Json);
        if (paperJson != _lastPaper && Write(_paperPath, paperJson)) _lastPaper = paperJson;
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), Json) : null;
        }
        catch
        {
            return null; // 손상된 파일은 무시하고 새로 시작
        }
    }

    private static bool Write(string path, string content)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
