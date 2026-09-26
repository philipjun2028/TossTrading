namespace TossTrading.Toss;

/// <summary>토스증권 Open API 연결 설정</summary>
public sealed class TossOptions
{
    public string BaseUrl { get; set; } = "https://openapi.tossinvest.com";
    public string WebSocketUrl { get; set; } = "wss://openapi-ws.tossinvest.com/ws/v1";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>계좌 일련번호 (GET /api/v1/accounts 의 accountSeq). 0 이면 첫 위탁계좌 자동 선택.</summary>
    public long AccountSeq { get; set; }

    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// API 그룹별 초당 호출 한도. 공개 자료 대략치보다 보수적으로 설정.
    /// 서버 응답 헤더 X-RateLimit-Limit 이 더 낮으면 자동으로 낮춘다.
    /// </summary>
    public int MarketDataPerSecond { get; set; } = 10;

    /// <summary>캔들 (MARKET_DATA_CHART, 공식 20/s)</summary>
    public int ChartPerSecond { get; set; } = 15;

    /// <summary>순위 (RANKING, 공식 5/s)</summary>
    public int RankingPerSecond { get; set; } = 4;

    /// <summary>전체 종목 목록 (STOCK_ALL, 공식 1/s)</summary>
    public int StockAllPerSecond { get; set; } = 1;
    public int StockPerSecond { get; set; } = 4;
    public int AccountPerSecond { get; set; } = 1;
    public int AssetPerSecond { get; set; } = 4;
    public int OrderQueryPerSecond { get; set; } = 3;
    public int AuthPerSecond { get; set; } = 2;

    public bool HasCredentials => !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}

/// <summary>호출 한도 그룹 (엔드포인트 → 그룹 매핑은 추정치, 공식 문서로 확인 필요)</summary>
/// <summary>토스 API 호출 한도 그룹 (공식 문서의 Rate Limits Group 과 대응)</summary>
public enum RateGroup { Auth, MarketData, Stock, Account, Asset, Order, OrderQuery, Chart, Ranking, StockAll }
