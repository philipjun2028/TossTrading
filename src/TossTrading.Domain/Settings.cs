namespace TossTrading.Domain;

/// <summary>종목별 봇 설정 (설계 문서 6.3). 퍼센트 값은 모두 "%" 단위.</summary>
public sealed class BotSettings
{
    // ---- 진입 ----
    public BotMode Mode { get; set; } = BotMode.ManualEntry;
    public EntryStrategyKind Strategy { get; set; } = EntryStrategyKind.Manual;
    public TimeOnly EntryStartTime { get; set; } = new(9, 5);
    public TimeOnly EntryEndTime { get; set; } = new(11, 0);

    /// <summary>진입 지정가 = 매도1호가 + N틱</summary>
    public int EntrySlippageTicks { get; set; } = 1;

    /// <summary>진입 주문 미체결 시 취소까지 초</summary>
    public int EntryTimeoutSeconds { get; set; } = 3;

    /// <summary>ORB 시가 범위 (분)</summary>
    public int OrbMinutes { get; set; } = 5;

    // ---- 자금 ----
    public SizingMode Sizing { get; set; } = SizingMode.RiskBased;

    /// <summary>리스크 기반: 거래당 계좌 대비 손실 허용 %</summary>
    public decimal RiskPerTradePct { get; set; } = 0.2m;

    /// <summary>고정 금액 방식 투입금</summary>
    public decimal FixedAmount { get; set; } = 1_000_000m;

    /// <summary>이 봇의 최대 투입금 (봇 목표/손실 % 의 기준 금액)</summary>
    public decimal MaxPositionAmount { get; set; } = 2_000_000m;

    // ---- 청산 ----
    public decimal StopLossPct { get; set; } = 1.5m;

    /// <summary>전략이 주는 구조적 손절(ORB 저가, VWAP 등)을 우선 사용</summary>
    public bool UseStructuralStop { get; set; } = true;

    /// <summary>1차 분할 익절 % (0 = 사용 안 함)</summary>
    public decimal PartialTakeProfitPct { get; set; } = 2.0m;

    /// <summary>1차 익절 시 매도 비율 %</summary>
    public decimal PartialTakeProfitRatioPct { get; set; } = 50m;

    /// <summary>최종 익절 % (0 = 트레일링에 맡김)</summary>
    public decimal TakeProfitPct { get; set; } = 4.0m;

    /// <summary>트레일링 활성화 수익 % (0 = 사용 안 함)</summary>
    public decimal TrailingActivationPct { get; set; } = 1.5m;

    /// <summary>트레일링 폭: 고점 대비 %</summary>
    public decimal TrailingDistancePct { get; set; } = 1.2m;

    /// <summary>+1R 도달 시 손절가를 본절(비용 포함)로</summary>
    public bool MoveStopToBreakEven { get; set; } = true;

    /// <summary>타임스탑: 진입 후 N분 내 진척 없으면 청산 (0 = 사용 안 함)</summary>
    public int TimeStopMinutes { get; set; } = 20;

    /// <summary>타임스탑 판정 기준: 최고 수익이 이 R 배수 미만이면 청산</summary>
    public decimal TimeStopMinProgressR { get; set; } = 0.5m;

    public TimeOnly ForceExitTime { get; set; } = new(15, 10);

    /// <summary>청산 지정가 = 매수1호가 - N틱</summary>
    public int ExitSlippageTicks { get; set; } = 2;

    /// <summary>청산 주문 미체결 시 시장가로 정정까지 초</summary>
    public int ExitTimeoutSeconds { get; set; } = 3;

    // ---- 봇 목표 / 정지 ----
    /// <summary>봇 누적 순실현손익이 MaxPositionAmount 의 이 % 에 도달하면 봇 완료 (0 = 사용 안 함)</summary>
    public decimal BotTargetProfitPct { get; set; } = 3.0m;

    /// <summary>봇 누적 순실현손실 한도 %</summary>
    public decimal BotMaxLossPct { get; set; } = 2.0m;

    public int MaxEntries { get; set; } = 3;
    public int CooldownSeconds { get; set; } = 300;

    // ---- 종가매매 / 익일 보유 ----
    /// <summary>장 마감 후에도 보유 (종가매매). 켜면 강제청산 시각·타임스탑 대신 익일 청산 규칙을 쓴다.</summary>
    public bool HoldOvernight { get; set; }

    public NextDayExitMode NextDayExitMode { get; set; } = NextDayExitMode.Managed;

    /// <summary>익일 이 시각까지 남은 수량을 매도 (Managed 모드)</summary>
    public TimeOnly NextDayExitTime { get; set; } = new(10, 0);

    /// <summary>종가매매 후보 조건: 당일 등락률 하한 %</summary>
    public decimal ClosingMinChangePct { get; set; } = 3m;

    /// <summary>종가매매 후보 조건: 당일 등락률 상한 % (상한가 근처 추격 방지)</summary>
    public decimal ClosingMaxChangePct { get; set; } = 20m;

    /// <summary>종가매매 후보 조건: 당일 고저 범위 내 위치 하한 (0~1, 1 = 고가 마감)</summary>
    public decimal ClosingMinRangePosition { get; set; } = 0.75m;

    /// <summary>KRX 종가 단일가 매매 시작 (15:20). 이후에는 접속매매 체결이 없다.</summary>
    public static readonly TimeOnly MarketCloseAuction = new(15, 20);

    public BotSettings Clone() => (BotSettings)MemberwiseClone();

    /// <summary>설정 유효성 검사. 문제 없으면 빈 목록.</summary>
    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        if (MaxPositionAmount <= 0) e.Add("최대 투입금은 0보다 커야 합니다.");
        if (StopLossPct <= 0) e.Add("손절 %는 0보다 커야 합니다.");
        if (Sizing == SizingMode.RiskBased && RiskPerTradePct <= 0) e.Add("거래당 리스크 %는 0보다 커야 합니다.");
        if (Sizing == SizingMode.FixedAmount && FixedAmount <= 0) e.Add("고정 투입금은 0보다 커야 합니다.");
        if (PartialTakeProfitRatioPct is < 0 or > 100) e.Add("분할 익절 비율은 0~100% 입니다.");
        if (MaxEntries <= 0) e.Add("최대 진입 횟수는 1 이상이어야 합니다.");
        if (EntryEndTime <= EntryStartTime) e.Add("진입 종료 시각이 시작 시각보다 늦어야 합니다.");
        if (Mode == BotMode.ManualEntry && Strategy != EntryStrategyKind.Manual)
            e.Add("수동진입 모드에서는 전략을 '수동'으로 두세요 (자동 신호는 반자동/완전자동에서 사용).");
        if (Mode != BotMode.ManualEntry && Strategy == EntryStrategyKind.Manual)
            e.Add("반자동/완전자동 모드에는 진입 전략을 선택해야 합니다.");
        if (Strategy == EntryStrategyKind.ClosingBet && !HoldOvernight)
            e.Add("종가매매 전략은 '익일 보유'를 켜야 합니다.");
        if (HoldOvernight && EntryEndTime > MarketCloseAuction)
            e.Add("익일 보유 봇은 진입 종료 시각이 15:20 (종가 단일가 시작) 이전이어야 합니다.");
        if (HoldOvernight && (NextDayExitTime <= Kst.MarketOpen || NextDayExitTime > MarketCloseAuction))
            e.Add("익일 청산 시각은 09:00 이후 15:20 이전이어야 합니다.");
        if (ClosingMinChangePct > ClosingMaxChangePct) e.Add("종가매매 등락률 하한이 상한보다 큽니다.");
        return e;
    }
}

/// <summary>계좌 레벨 리스크 한도 (설계 문서 7.2)</summary>
public sealed class RiskSettings
{
    public decimal DailyLossLimitPct { get; set; } = 1.5m;

    /// <summary>일 목표 수익 % (0 = 사용 안 함). 도달 시 신규 진입 중지.</summary>
    public decimal DailyProfitTargetPct { get; set; } = 0m;

    public int MaxConcurrentPositions { get; set; } = 4;
    public decimal MaxTotalExposurePct { get; set; } = 60m;

    /// <summary>1회 주문 금액 상한 (1억 이상은 토스 확인 플래그 필요 → 기본 5천만)</summary>
    public decimal MaxOrderAmount { get; set; } = 50_000_000m;

    public int MaxConsecutiveLosses { get; set; } = 4;
    public int ConsecutiveLossCooldownMinutes { get; set; } = 30;
    public int MaxOrderErrorsPerBot { get; set; } = 3;
    public bool FlattenOnDailyLossLimit { get; set; } = false;

    /// <summary>09:00~09:05 동시호가 직후 변동성 구간 신규 진입 차단</summary>
    public bool BlockEntriesDuringOpeningMinutes { get; set; } = false;

    public RiskSettings Clone() => (RiskSettings)MemberwiseClone();
}

/// <summary>스캐너 필터 (설계 문서 5.2)</summary>
public sealed class ScannerSettings
{
    public int PollSeconds { get; set; } = 5;
    public decimal MinPrice { get; set; } = 1_000m;
    public decimal MinChangePct { get; set; } = 3m;
    public decimal MaxChangePct { get; set; } = 20m;

    /// <summary>시간 보정 전 최소 거래대금 (원)</summary>
    public decimal MinTradingAmount { get; set; } = 3_000_000_000m;

    public decimal MinRvol { get; set; } = 2.0m;
    public decimal MaxTickCostPct { get; set; } = 0.15m;
    public int MaxSpreadTicks { get; set; } = 3;
    public bool ExcludeNonCommonStock { get; set; } = true;
    public int MaxCandidates { get; set; } = 30;

    /// <summary>실시간 체결 구독할 상위 후보 수 (웹소켓 토픽 예산)</summary>
    public int LiveSubscribeTop { get; set; } = 20;

    // ---- 종가매매 모드 ----
    public ScanMode Mode { get; set; } = ScanMode.DayTrading;
    public decimal ClosingMinChangePct { get; set; } = 3m;
    public decimal ClosingMaxChangePct { get; set; } = 20m;

    /// <summary>당일 고저 범위 내 위치 하한 (0~1)</summary>
    public decimal ClosingMinRangePosition { get; set; } = 0.75m;

    /// <summary>한 번 스캔할 때 분봉을 새로 조회할 최대 종목 수 (호출 한도 보호)</summary>
    public int ClosingBarsPerCycle { get; set; } = 8;

    /// <summary>분봉 캐시 갱신 주기 (초)</summary>
    public int ClosingBarsRefreshSeconds { get; set; } = 60;

    public ScannerSettings Clone() => (ScannerSettings)MemberwiseClone();
}

/// <summary>자동 운용이 쓰는 봇 설정 묶음 (오전·장중·종가 프리셋). 모두 완전자동.</summary>
public static class BotPresets
{
    public static Dictionary<string, BotSettings> CreateDefaults() => new()
    {
        ["ORB 표준"] = new BotSettings
        {
            Mode = BotMode.FullAuto, Strategy = EntryStrategyKind.OpeningRangeBreakout,
        },
        ["VWAP 눌림 표준"] = new BotSettings
        {
            Mode = BotMode.FullAuto, Strategy = EntryStrategyKind.VwapReclaim,
            EntryEndTime = new TimeOnly(14, 0),
        },
        ["고가돌파 공격형"] = new BotSettings
        {
            Mode = BotMode.FullAuto, Strategy = EntryStrategyKind.HighBreakout,
            StopLossPct = 2.0m, TakeProfitPct = 6m, TrailingDistancePct = 1.8m, EntryEndTime = new TimeOnly(14, 0),
        },
        ["종가베팅 (익일 매도)"] = new BotSettings
        {
            Mode = BotMode.FullAuto, Strategy = EntryStrategyKind.ClosingBet,
            EntryStartTime = new TimeOnly(15, 0), EntryEndTime = new TimeOnly(15, 19),
            HoldOvernight = true, NextDayExitMode = NextDayExitMode.Managed, NextDayExitTime = new TimeOnly(10, 0),
            RiskPerTradePct = 0.2m, StopLossPct = 3m, UseStructuralStop = false,
            PartialTakeProfitPct = 2m, TakeProfitPct = 5m, TrailingActivationPct = 2.5m, TrailingDistancePct = 1.5m,
            TimeStopMinutes = 0, MaxEntries = 1, BotTargetProfitPct = 0, BotMaxLossPct = 0,
        },
        ["보수형"] = new BotSettings
        {
            Mode = BotMode.FullAuto, Strategy = EntryStrategyKind.OpeningRangeBreakout,
            RiskPerTradePct = 0.15m, StopLossPct = 1.2m, MaxEntries = 2, BotTargetProfitPct = 2m, BotMaxLossPct = 1.5m,
        },
    };
}

/// <summary>
/// 자동 운용 (종목 자동 선정 + 자동 매매). 장중에는 단타 후보를 골라 단타 봇을 돌리고,
/// 종가매매 시간이 되면 단타를 정리하고 종가 후보를 골라 종가 베팅(익일 매도)을 진행한다.
/// </summary>
public sealed class AutoPilotSettings
{
    public bool Enabled { get; set; }

    // ---- 단타 ----
    public bool DayTradingEnabled { get; set; } = true;

    /// <summary>단타 봇 선정 시작 (장 초반 변동성 회피용 여유)</summary>
    public TimeOnly DayStartTime { get; set; } = new(9, 5);

    /// <summary>이 시각 전에 추가되는 봇은 오전 프리셋(ORB 등), 이후는 장중 프리셋(VWAP 눌림 등)</summary>
    public TimeOnly MorningUntil { get; set; } = new(10, 0);

    /// <summary>단타 신규 진입 마감</summary>
    public TimeOnly DayEntryEndTime { get; set; } = new(14, 30);

    /// <summary>단타 보유분 정리 (종가매매 자금 확보)</summary>
    public TimeOnly DayExitTime { get; set; } = new(14, 50);

    public int MaxDayBots { get; set; } = 3;

    /// <summary>단타 후보 최소 점수 (0 = 제한 없음)</summary>
    public decimal MinDayScore { get; set; } = 50m;

    /// <summary>이 시간 동안 진입이 없고 상위 후보에서 밀려난 단타 봇은 다른 종목으로 교체 (0 = 교체 안 함)</summary>
    public int IdleReplaceMinutes { get; set; } = 30;

    public string MorningPreset { get; set; } = "ORB 표준";

    /// <summary>
    /// 오전 이후 단타 프리셋. 기본 "사용 안 함": VWAP 눌림은 2026-01~09 토스 백테스트에서
    /// 상·하반기 모두 Profit Factor 0.3 대로 손실이 커서 기본으로 끔.
    /// </summary>
    public string DayPreset { get; set; } = NoPreset;

    /// <summary>프리셋 "사용 안 함" 표시값</summary>
    public const string NoPreset = "(사용 안 함)";

    // ---- 종가매매 ----
    public bool ClosingEnabled { get; set; } = true;

    /// <summary>스캐너를 종가매매 후보 모드로 전환 (분봉 수집에 여유를 둔다)</summary>
    public TimeOnly ClosingScanTime { get; set; } = new(14, 40);

    /// <summary>종가 봇 선정 시작 / 마감 (진입 자체는 종가베팅 전략이 15:00~15:19 에 판단)</summary>
    public TimeOnly ClosingSelectTime { get; set; } = new(14, 55);
    public TimeOnly ClosingSelectEndTime { get; set; } = new(15, 15);

    public int MaxClosingBots { get; set; } = 2;

    /// <summary>종가 후보 조건 최소 통과 수 (0 = 전부 통과)</summary>
    public int ClosingMinPassed { get; set; }

    public string ClosingPreset { get; set; } = "종가베팅 (익일 매도)";

    public AutoPilotSettings Clone() => (AutoPilotSettings)MemberwiseClone();

    public IReadOnlyList<string> Validate()
    {
        var e = new List<string>();
        if (!(DayStartTime < DayEntryEndTime && DayEntryEndTime <= DayExitTime))
            e.Add("자동 운용: 단타 시작 < 신규 진입 마감 ≤ 단타 정리 순서여야 합니다.");
        if (!(ClosingScanTime <= ClosingSelectTime && ClosingSelectTime < ClosingSelectEndTime && ClosingSelectEndTime < BotSettings.MarketCloseAuction))
            e.Add("자동 운용: 종가 스캔 ≤ 종가 선정 시작 < 선정 마감 < 15:20 순서여야 합니다.");
        if (DayTradingEnabled && ClosingEnabled && DayExitTime > ClosingSelectTime)
            e.Add("자동 운용: 단타 정리 시각이 종가 선정 시작보다 늦으면 자금이 겹칩니다.");
        if (MaxDayBots < 0 || MaxClosingBots < 0) e.Add("자동 운용: 봇 수는 0 이상이어야 합니다.");
        return e;
    }
}

/// <summary>자동 운용 설정 + 실제로 사용할 봇 설정 (프리셋 이름을 풀어 둔 것)</summary>
public sealed record AutoPilotPlan(AutoPilotSettings Settings, BotSettings Morning, BotSettings? Day, BotSettings Closing)
{
    public static AutoPilotPlan Default(bool enabled = false) =>
        FromPresets(new AutoPilotSettings { Enabled = enabled }, BotPresets.CreateDefaults());

    /// <summary>프리셋 이름으로 봇 설정을 찾는다. 없거나 맞지 않으면 기본 프리셋을 쓴다.</summary>
    public static AutoPilotPlan FromPresets(AutoPilotSettings s, IReadOnlyDictionary<string, BotSettings> presets)
    {
        var defaults = BotPresets.CreateDefaults();
        BotSettings Pick(string name, string fallback, Func<BotSettings, bool> ok) =>
            (presets.TryGetValue(name, out var p) && ok(p) ? p : defaults[fallback]).Clone();

        var day = Pick(s.MorningPreset, "ORB 표준", p => p.Strategy is not (EntryStrategyKind.Manual or EntryStrategyKind.ClosingBet));
        BotSettings? day2 = string.IsNullOrEmpty(s.DayPreset) || s.DayPreset == AutoPilotSettings.NoPreset
            ? null
            : Pick(s.DayPreset, "VWAP 눌림 표준", p => p.Strategy is not (EntryStrategyKind.Manual or EntryStrategyKind.ClosingBet));
        var closing = Pick(s.ClosingPreset, "종가베팅 (익일 매도)", p => p.Strategy == EntryStrategyKind.ClosingBet);
        return new AutoPilotPlan(s.Clone(), day, day2, closing);
    }
}
