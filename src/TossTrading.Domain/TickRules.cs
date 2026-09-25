namespace TossTrading.Domain;

/// <summary>
/// 호가단위 규칙. 국내는 2023년 개편 이후 KOSPI/KOSDAQ 공통 테이블.
/// 미국은 $1 이상 0.01, 미만 0.0001.
/// </summary>
public static class TickRules
{
    public static decimal TickSize(decimal price, MarketCountry market = MarketCountry.KR)
    {
        if (market == MarketCountry.US)
            return price >= 1m ? 0.01m : 0.0001m;

        return price switch
        {
            < 2_000m => 1m,
            < 5_000m => 5m,
            < 20_000m => 10m,
            < 50_000m => 50m,
            < 200_000m => 100m,
            < 500_000m => 500m,
            _ => 1_000m,
        };
    }

    /// <summary>가격 이하의 가장 가까운 유효 호가</summary>
    public static decimal RoundDown(decimal price, MarketCountry market = MarketCountry.KR)
    {
        if (price <= 0) return 0;
        var tick = TickSize(price, market);
        return Math.Floor(price / tick) * tick;
    }

    /// <summary>가격 이상의 가장 가까운 유효 호가</summary>
    public static decimal RoundUp(decimal price, MarketCountry market = MarketCountry.KR)
    {
        if (price <= 0) return 0;
        var down = RoundDown(price, market);
        return down == price ? price : down + TickSize(down, market);
    }

    /// <summary>유효 호가에서 n틱 이동 (음수면 아래로). 호가단위 경계를 정확히 넘는다.</summary>
    public static decimal AddTicks(decimal price, int ticks, MarketCountry market = MarketCountry.KR)
    {
        var p = RoundDown(price, market);
        if (ticks > 0)
        {
            for (var i = 0; i < ticks; i++) p += TickSize(p, market);
        }
        else
        {
            for (var i = 0; i < -ticks && p > 0; i++)
            {
                // 아래로 내려갈 때는 "바로 아래 가격"의 호가단위를 써야 한다 (2,000 → 1,999)
                var below = TickSize(p - 0.0001m, market);
                p -= below;
            }
        }
        return Math.Max(p, 0);
    }

    /// <summary>두 가격 사이 틱 수 (대략, 같은 호가 구간 가정)</summary>
    public static int TicksBetween(decimal low, decimal high, MarketCountry market = MarketCountry.KR)
    {
        if (high <= low) return 0;
        var count = 0;
        var p = RoundDown(low, market);
        while (p < high && count < 10_000)
        {
            p += TickSize(p, market);
            count++;
        }
        return count;
    }

    /// <summary>호가단위 / 가격 (틱 비용률, 비율)</summary>
    public static decimal TickCostRate(decimal price, MarketCountry market = MarketCountry.KR) =>
        price <= 0 ? 0 : TickSize(price, market) / price;

    public static decimal Clamp(decimal price, decimal? lower, decimal? upper)
    {
        if (upper is { } u && u > 0 && price > u) price = u;
        if (lower is { } l && l > 0 && price < l) price = l;
        return price;
    }
}
