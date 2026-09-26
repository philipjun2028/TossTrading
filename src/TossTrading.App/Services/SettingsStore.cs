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
    public AutoPilotSettings AutoPilot { get; set; } = new() { Enabled = true };

    /// <summary>설정 파일 형식 버전 (기본값이 바뀐 항목을 한 번만 옮기기 위해)</summary>
    public int SettingsVersion { get; set; } // 없으면 0 → 불러올 때 이전 기본값을 옮긴다

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
                    MigratePresets(s);
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

    /// <summary>
    /// 종목 자동 선정으로 바뀐 뒤: 수동 진입 전용 프리셋은 쓸 곳이 없어 지우고, 나머지는 완전자동으로 맞춘다.
    /// </summary>
    public const int CurrentVersion = 2;

    public static void MigratePresets(AppSettings s)
    {
        if (s.SettingsVersion < 2)
        {
            // v2: 장중 VWAP 눌림은 백테스트(2026-01~09) PF 0.3 대 → 기본값이던 경우 "사용 안 함"으로
            if (s.AutoPilot.DayPreset == "VWAP 눌림 표준") s.AutoPilot.DayPreset = AutoPilotSettings.NoPreset;
            s.SettingsVersion = 2;
        }
        foreach (var name in s.Presets.Where(kv => kv.Value.Strategy == EntryStrategyKind.Manual).Select(kv => kv.Key).ToList())
            s.Presets.Remove(name);
        foreach (var p in s.Presets.Values) p.Mode = BotMode.FullAuto;
        foreach (var (name, p) in BotPresets.CreateDefaults())
            s.Presets.TryAdd(name, p); // 자동 운용 기본 프리셋이 지워졌으면 되살림
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
