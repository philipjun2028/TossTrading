namespace TossTrading.Domain;

/// <summary>포지션 사이징 (설계 문서 7.1)</summary>
public static class PositionSizer
{
    /// <summary>
    /// 매수 수량 계산.
    /// 리스크 기반: floor(계좌 × 리스크% ÷ (진입가 − 손절가)).
    /// 모든 방식은 봇 최대 투입금·매수가능금액·주문 상한으로 제한된다.
    /// </summary>
    public static decimal Quantity(
        BotSettings s,
        decimal equity,
        decimal entryPrice,
        decimal stopPrice,
        decimal buyingPower,
        decimal maxOrderAmount)
    {
        if (entryPrice <= 0) return 0;

        var cap = Math.Min(Math.Min(s.MaxPositionAmount, buyingPower), maxOrderAmount);
        if (cap <= 0) return 0;

        decimal qty;
        if (s.Sizing == SizingMode.FixedAmount)
        {
            qty = Math.Floor(Math.Min(s.FixedAmount, cap) / entryPrice);
        }
        else
        {
            var perShareRisk = entryPrice - stopPrice;
            if (perShareRisk <= 0) return 0;
            var riskAmount = equity * s.RiskPerTradePct / 100m;
            qty = Math.Floor(riskAmount / perShareRisk);
            qty = Math.Min(qty, Math.Floor(cap / entryPrice));
        }
        return Math.Max(qty, 0);
    }
}
