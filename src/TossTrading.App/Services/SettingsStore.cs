using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TossTrading.Domain;

namespace TossTrading.App.Services;

/// <summary>앱 전체 설정 (%LocalAppData%\TossTrading\settings.json)</summary>
public sealed class AppSettings
{
    public DataSourceKind DataSource { get; set; } = DataSourceKind.Simulation;
    public ExecutionMode Execution { get; set; } = ExecutionMode.Paper;

    // 토스 연결 (Secret 은 DPAPI 로 암호화해 저장)
    public string TossClientId { get; set; } = "";
    public string? TossClientSecretProtected { get; set; }
    public long TossAccountSeq { get; set; }
    public string TossBaseUrl { get; set; } = "https://openapi.tossinvest.com";
    public string TossWebSocketUrl { get; set; } = "wss://openapi-ws.tossinvest.com/ws/v1";

    public RiskSettings Risk { get; set; } = new();
    public ScannerSettings Scanner { get; set; } = new();
    public CostSettings Cost { get; set; } = new();
    public Dictionary<string, BotSettings> Presets { get; set; } = BotPresets.CreateDefaults();

    /// <summary>자동 운용 (종목 자동 선정 + 단타 → 종가매매 자동 전환)</summary>
    public AutoPilotSettings AutoPilot { get; set; } = new();

    public AutoPilotPlan AutoPilotPlan() => Domain.AutoPilotPlan.FromPresets(AutoPilot, Presets);

    public decimal PaperStartingCash { get; set; } = 10_000_000m;
    public decimal CapitalOverride { get; set; }
    public double SimulationSpeed { get; set; } = 10;

    /// <summary>시뮬레이션 시작 가상 시각 (종가매매 연습은 14:30 등으로)</summary>
    public TimeOnly SimulationStartTime { get; set; } = new(9, 0);
    public bool RecordTicks { get; set; } = true;
    public int OrderRatePerSecond { get; set; } = 5;
    public int OpeningOrderRatePerSecond { get; set; } = 2;

    [JsonIgnore]
    public string TossClientSecret
    {
        get => SecretProtector.Unprotect(TossClientSecretProtected);
        set => TossClientSecretProtected = SecretProtector.Protect(value);
    }
}

/// <summary>Windows DPAPI (현재 사용자 범위) 로 비밀값 보호</summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TossTrading.v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return ""; // 다른 PC/사용자에서 복사된 설정 → 다시 입력 필요
        }
        catch (FormatException)
        {
            return "";
        }
    }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TossTrading");

    private static string FilePath => Path.Combine(DataDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json);
                if (s is not null)
                {
                    if (s.Presets.Count == 0) s.Presets = BotPresets.CreateDefaults();
                    return s;
                }
            }
        }
        catch
        {
            // 손상된 설정 → 기본값
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Json), Json)!;
}
