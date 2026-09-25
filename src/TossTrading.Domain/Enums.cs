namespace TossTrading.Domain;

public enum MarketCountry { KR, US }

public enum OrderSide { Buy, Sell }

public enum OrderType { Limit, Market }

public enum OrderStatus
{
    /// <summary>로컬에서 전송 대기/전송 중 (브로커 응답 전)</summary>
    Submitting,
    Pending,
    PartialFilled,
    Filled,
    PendingCancel,
    PendingReplace,
    Canceled,
    Rejected,
    Replaced,
    CancelRejected,
    ReplaceRejected,
}

/// <summary>주문 큐 우선순위. 값이 작을수록 먼저 나간다.</summary>
public enum OrderPriority
{
    Emergency = 0,
    CancelOrModify = 1,
    Exit = 2,
    Entry = 3,
}

public enum ExecutionMode { Paper, Live }

public enum DataSourceKind { Simulation, Toss }

/// <summary>봇 운용 모드 (설계 문서 2.2)</summary>
public enum BotMode
{
    /// <summary>A. 사용자가 매수, 봇이 청산 관리</summary>
    ManualEntry,
    /// <summary>B. 봇이 신호 → 사용자가 승인</summary>
    SemiAuto,
    /// <summary>C. 봇이 신호 시 즉시 진입</summary>
    FullAuto,
}

public enum EntryStrategyKind
{
    Manual,
    OpeningRangeBreakout,
    VwapReclaim,
    HighBreakout,
    /// <summary>종가매매: 장 마감 전 강세 종목 매수 → 익일 매도</summary>
    ClosingBet,
}

/// <summary>익일 보유 포지션의 청산 방식</summary>
public enum NextDayExitMode
{
    /// <summary>익일 장 시작 직후 전량 매도</summary>
    AtOpen,
    /// <summary>손절/익절/트레일링으로 관리하고, 청산 시각에 잔량 매도</summary>
    Managed,
}

public enum SizingMode { RiskBased, FixedAmount }

/// <summary>스캐너 모드: 단타(Stocks in Play) / 종가매매 후보</summary>
public enum ScanMode { DayTrading, ClosingBet }

public enum BotState
{
    Idle,
    Watching,
    SignalPending,
    EntryPending,
    InPosition,
    ExitPending,
    Cooldown,
    Suspended,
    Completed,
    Halted,
    Stopped,
}

public enum RankingType { TradingAmount, TradingVolume, TopGainers }

public static class OrderStatusExtensions
{
    public static bool IsTerminal(this OrderStatus s) =>
        s is OrderStatus.Filled or OrderStatus.Canceled or OrderStatus.Rejected or OrderStatus.Replaced;
}

public static class BotStateExtensions
{
    public static bool IsFinished(this BotState s) =>
        s is BotState.Completed or BotState.Halted or BotState.Stopped;

    public static string ToKorean(this BotState s) => s switch
    {
        BotState.Idle => "대기",
        BotState.Watching => "감시중",
        BotState.SignalPending => "승인대기",
        BotState.EntryPending => "매수주문중",
        BotState.InPosition => "보유중",
        BotState.ExitPending => "매도주문중",
        BotState.Cooldown => "쿨다운",
        BotState.Suspended => "일시중단",
        BotState.Completed => "완료",
        BotState.Halted => "손실정지",
        BotState.Stopped => "정지",
        _ => s.ToString(),
    };
}
