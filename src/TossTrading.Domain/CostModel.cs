namespace TossTrading.Domain;

/// <summary>거래 비용 파라미터 (설계 문서 4장). 모두 "비율" 이 아니라 "%" 단위로 저장한다.</summary>
public sealed class CostSettings
{
    /// <summary>매매 수수료율 % (편도). 기본 0.015%</summary>
    public decimal CommissionPct { get; set; } = 0.015m;

    /// <summary>매도 시 증권거래세+농특세 % (2026년 코스피/코스닥 0.20%)</summary>
    public decimal SellTaxPct { get; set; } = 0.20m;

    /// <summary>비용 추정용 슬리피지 틱 수 (편도)</summary>
    public int SlippageTicks { get; set; } = 1;

    public CostSettings Clone() => (CostSettings)MemberwiseClone();
}

public sealed class CostModel
{
    private readonly CostSettings _s;

    public CostModel(CostSettings settings) => _s = settings;

    public decimal CommissionRate => _s.CommissionPct / 100m;
    public decimal SellTaxRate => _s.SellTaxPct / 100m;

    public decimal Commission(decimal amount) => Math.Floor(amount * CommissionRate);

    public decimal SellTax(decimal amount) => Math.Floor(amount * SellTaxRate);

    /// <summary>매수 체결 시 비용</summary>
    public decimal BuyCost(decimal price, decimal quantity) => Commission(price * quantity);

    /// <summary>매도 체결 시 비용 (수수료 + 세금)</summary>
    public decimal SellCost(decimal price, decimal quantity)
    {
        var amount = price * quantity;
        return Commission(amount) + SellTax(amount);
    }

    /// <summary>
    /// 왕복 비용률(비율). 수수료×2 + 세금 + 슬리피지(틱×2).
    /// 예) 10,000원 종목: 0.00015×2 + 0.002 + 0.001×2 = 0.0043 (0.43%)
    /// </summary>
    public decimal RoundTripCostRate(decimal price, MarketCountry market = MarketCountry.KR) =>
        CommissionRate * 2 + SellTaxRate + _s.SlippageTicks * 2 * TickRules.TickCostRate(price, market);

    /// <summary>순손익이 0이 되는 매도가 (슬리피지 제외)</summary>
    public decimal BreakEvenPrice(decimal averageBuyPrice) =>
        averageBuyPrice * (1 + CommissionRate) / (1 - CommissionRate - SellTaxRate);

    /// <summary>매도 체결분의 순손익. 매수 수수료는 평균단가 기준으로 배분.</summary>
    public decimal NetPnl(decimal averageBuyPrice, decimal sellPrice, decimal quantity) =>
        (sellPrice - averageBuyPrice) * quantity - BuyCost(averageBuyPrice, quantity) - SellCost(sellPrice, quantity);
}
