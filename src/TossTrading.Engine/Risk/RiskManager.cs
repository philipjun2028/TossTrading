using TossTrading.Domain;

namespace TossTrading.Engine.Risk;

/// <summary>
/// 계좌 레벨 리스크 관리 (설계 문서 7.2~7.3). 모든 봇 위의 상위 규칙.
/// 엔진 이벤트 루프에서만 호출된다.
/// </summary>
public sealed class RiskManager
{
    private int _consecutiveLosses;
    private DateTimeOffset _lossCooldownUntil = DateTimeOffset.MinValue;

    public RiskManager(RiskSettings settings) => Settings = settings.Clone();

    public RiskSettings Settings { get; private set; }
    public decimal StartEquity { get; private set; }
    public decimal RealizedToday { get; private set; }
    public bool KillSwitchActive { get; private set; }
    public bool DailyLossLimitHit { get; private set; }
    public bool DailyTargetHit { get; private set; }

    public void UpdateSettings(RiskSettings s) => Settings = s.Clone();

    public void SetStartEquity(decimal equity)
    {
        if (equity > 0) StartEquity = equity;
    }

    public void ActivateKillSwitch() => KillSwitchActive = true;

    public void ResetKillSwitch() => KillSwitchActive = false;

    public decimal DailyPnl(decimal unrealized) => RealizedToday + unrealized;

    public decimal DailyPnlPct(decimal unrealized) =>
        StartEquity > 0 ? DailyPnl(unrealized) / StartEquity * 100m : 0;

    /// <summary>신규 진입 가능 여부.</summary>
    public (bool Allowed, string? Reason) CanEnter(
        DateTimeOffset now, decimal orderAmount, int openPositions, decimal currentExposure, decimal unrealized)
    {
        var reason = BlockReason(now, unrealized);
        if (reason is not null) return (false, reason);

        if (openPositions >= Settings.MaxConcurrentPositions)
            return (false, $"동시 보유 한도 {Settings.MaxConcurrentPositions}종목");
        if (orderAmount > Settings.MaxOrderAmount)
            return (false, $"1회 주문 상한 {Settings.MaxOrderAmount:N0}원 초과");
        if (orderAmount >= 100_000_000m)
            return (false, "1억원 이상 주문은 차단 (토스 고액주문 확인 필요)");
        if (StartEquity > 0 && currentExposure + orderAmount > StartEquity * Settings.MaxTotalExposurePct / 100m)
            return (false, $"총 노출 한도 {Settings.MaxTotalExposurePct}% 초과");
        return (true, null);
    }

    /// <summary>현재 신규 진입을 막는 계좌 레벨 사유 (없으면 null)</summary>
    public string? BlockReason(DateTimeOffset now, decimal unrealized)
    {
        if (KillSwitchActive) return "킬스위치 작동 중";
        if (DailyLossLimitHit) return $"일 손실 한도 {Settings.DailyLossLimitPct}% 도달";
        if (DailyTargetHit) return $"일 목표 수익 {Settings.DailyProfitTargetPct}% 달성";
        if (now < _lossCooldownUntil) return $"{_consecutiveLosses}연속 손실 쿨다운 ~{Kst.TimeOf(_lossCooldownUntil):HH\\:mm}";
        if (Settings.BlockEntriesDuringOpeningMinutes)
        {
            var t = Kst.TimeOf(now);
            if (t >= Kst.MarketOpen && t < new TimeOnly(9, 5)) return "개장 직후 5분 진입 차단";
        }
        return null;
    }

    public void OnTradeClosed(ClosedTrade trade, DateTimeOffset now)
    {
        RealizedToday += trade.NetPnl;
        if (trade.NetPnl < 0)
        {
            _consecutiveLosses++;
            if (Settings.MaxConsecutiveLosses > 0 && _consecutiveLosses >= Settings.MaxConsecutiveLosses)
                _lossCooldownUntil = now.AddMinutes(Settings.ConsecutiveLossCooldownMinutes);
        }
        else
        {
            _consecutiveLosses = 0;
        }
    }

    /// <summary>일 손익 한도 점검. 손실 한도에 "새로" 도달하면 true (엔진이 청산 여부 결정).</summary>
    public bool CheckDailyLimits(decimal unrealized)
    {
        if (StartEquity <= 0) return false;
        var pct = DailyPnlPct(unrealized);
        if (!DailyLossLimitHit && Settings.DailyLossLimitPct > 0 && pct <= -Settings.DailyLossLimitPct)
        {
            DailyLossLimitHit = true;
            return true;
        }
        if (!DailyTargetHit && Settings.DailyProfitTargetPct > 0 && pct >= Settings.DailyProfitTargetPct)
            DailyTargetHit = true;
        return false;
    }
}
