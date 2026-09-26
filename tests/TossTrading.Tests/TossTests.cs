using System.Net;
using System.Text;
using System.Text.Json;
using TossTrading.Domain;
using TossTrading.Toss;

namespace TossTrading.Tests;

/// <summary>요청을 기록하고 미리 정한 응답을 돌려주는 가짜 HTTP 핸들러</summary>
internal sealed class FakeHandler : HttpMessageHandler
{
    public readonly List<(HttpRequestMessage Request, string? Body)> Requests = new();
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return Respond(request);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class TossRestClientTests
{
    private const string Token = """{"access_token":"tok-1","token_type":"Bearer","expires_in":3600}""";

    private static (TossRestClient Client, FakeHandler Handler) Create(long accountSeq = 3)
    {
        var handler = new FakeHandler();
        var options = new TossOptions { ClientId = "id", ClientSecret = "secret", AccountSeq = accountSeq, BaseUrl = "https://example.test" };
        return (new TossRestClient(options, new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task ParsesResultEnvelopeWithStringNumbers()
    {
        var (client, handler) = Create();
        handler.Respond = req => req.RequestUri!.AbsolutePath switch
        {
            "/oauth2/token" => FakeHandler.Json(Token),
            "/api/v1/rankings" => FakeHandler.Json("""
                {"result":{"rankedAt":"2026-09-03T19:59:56.515+09:00","rankings":[
                  {"rank":1,"symbol":"459550","currency":"KRW","price":{"lastPrice":"2570","basePrice":"1979","changeRate":"0.2986"},
                   "tradingVolume":"13640212","tradingAmount":"31835155684"}]}}
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var r = await client.GetRankingsAsync("MARKET_TRADING_AMOUNT", "KR", "realtime", 5, true, TestContext.Current.CancellationToken);
        Assert.Single(r.Rankings);
        Assert.Equal(2570m, r.Rankings[0].Price.LastPrice);
        Assert.Equal(0.2986m, r.Rankings[0].Price.ChangeRate);
        Assert.Equal(31_835_155_684m, r.Rankings[0].TradingAmount);

        var tokenReq = handler.Requests[0];
        Assert.Equal("grant_type=client_credentials&client_id=id&client_secret=secret", tokenReq.Body);
        var apiReq = handler.Requests[1].Request;
        Assert.Equal("Bearer tok-1", apiReq.Headers.Authorization!.ToString());
        Assert.Contains("type=MARKET_TRADING_AMOUNT", apiReq.RequestUri!.Query);
        Assert.Contains("excludeInvestmentCaution=true", apiReq.RequestUri!.Query);
        Assert.False(apiReq.Headers.Contains("X-Tossinvest-Account"));
    }

    [Fact]
    public async Task AccountApisSendAccountHeaderAndOrderBodyUsesStrings()
    {
        var (client, handler) = Create(accountSeq: 7);
        handler.Respond = req => req.RequestUri!.AbsolutePath switch
        {
            "/oauth2/token" => FakeHandler.Json(Token),
            "/api/v1/orders" => FakeHandler.Json("""{"result":{"orderId":"ORD1","clientOrderId":"abc"}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var res = await client.PlaceOrderAsync(new PlaceOrderBody
        {
            Symbol = "005930", Side = "BUY", OrderType = "LIMIT", Quantity = "10", Price = "70000", TimeInForce = "DAY", ClientOrderId = "abc",
        }, TestContext.Current.CancellationToken);

        Assert.Equal("ORD1", res.OrderId);
        var (req, body) = handler.Requests[1];
        Assert.Equal("7", req.Headers.GetValues("X-Tossinvest-Account").Single());
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("005930", doc.RootElement.GetProperty("symbol").GetString());
        Assert.Equal("10", doc.RootElement.GetProperty("quantity").GetString());
        Assert.Equal("70000", doc.RootElement.GetProperty("price").GetString());
        Assert.Equal("abc", doc.RootElement.GetProperty("clientOrderId").GetString());
        Assert.False(doc.RootElement.TryGetProperty("confirmHighValueOrder", out _));
    }

    [Fact]
    public async Task ErrorEnvelopeBecomesTossApiException()
    {
        var (client, handler) = Create();
        handler.Respond = req => req.RequestUri!.AbsolutePath == "/oauth2/token"
            ? FakeHandler.Json(Token)
            : FakeHandler.Json("""{"error":{"requestId":"r1","code":"insufficient-buying-power","message":"매수 가능 금액 부족"}}""", HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<TossApiException>(() => client.PlaceOrderAsync(new PlaceOrderBody { Symbol = "005930", Side = "BUY", OrderType = "MARKET", Quantity = "1" }, TestContext.Current.CancellationToken));
        Assert.Equal("insufficient-buying-power", ex.Code);
        Assert.Equal("r1", ex.RequestId);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task ExpiredTokenIsRefreshedOnceForGet()
    {
        var (client, handler) = Create();
        var tokenCalls = 0;
        var apiCalls = 0;
        handler.Respond = req =>
        {
            if (req.RequestUri!.AbsolutePath == "/oauth2/token")
                return FakeHandler.Json($$"""{"access_token":"tok-{{++tokenCalls}}","expires_in":3600}""");
            return ++apiCalls == 1
                ? FakeHandler.Json("""{"error":{"code":"expired-token","message":"expired"}}""", HttpStatusCode.Unauthorized)
                : FakeHandler.Json("""{"result":[{"symbol":"005930","timestamp":null,"lastPrice":"248000","currency":"KRW"}]}""");
        };

        var prices = await client.GetPricesAsync(new[] { "005930" }, TestContext.Current.CancellationToken);
        Assert.Equal(248_000m, prices[0].LastPrice);
        Assert.Equal(2, tokenCalls);
        Assert.Equal("Bearer tok-2", handler.Requests.Last().Request.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task ForbiddenTokenMentionsIpAllowList()
    {
        var (client, handler) = Create();
        handler.Respond = _ => FakeHandler.Json("""{"error":"access_denied","error_description":"IP address not allowed"}""", HttpStatusCode.Forbidden);
        var ex = await Assert.ThrowsAsync<TossApiException>(() => client.GetPricesAsync(new[] { "005930" }, TestContext.Current.CancellationToken));
        Assert.Equal("access_denied", ex.Code);
        Assert.Contains("허용 IP", ex.Message);
    }

    private static HttpResponseMessage Gzip(string json, HttpStatusCode status)
    {
        using var ms = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(json));
        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        return new HttpResponseMessage(status) { Content = content };
    }

    [Fact]
    public async Task GzipErrorBodyIsDecodedNotGarbled()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Gzip("""{"error":"invalid_client","error_description":"클라이언트 인증 실패"}""", HttpStatusCode.Unauthorized);
        var ex = await Assert.ThrowsAsync<TossApiException>(() => client.GetPricesAsync(new[] { "005930" }, TestContext.Current.CancellationToken));
        Assert.Equal("invalid_client", ex.Code);
        Assert.Contains("클라이언트 인증 실패", ex.Message);
        Assert.Contains("Client ID/Secret", ex.Message);
        Assert.DoesNotContain("\uFFFD", ex.Message);
    }

    [Fact]
    public async Task TokenFallsBackToBasicAuthAndTrimsSecret()
    {
        var handler = new FakeHandler();
        var options = new TossOptions { ClientId = " id ", ClientSecret = "secret\r\n", BaseUrl = "https://example.test" };
        var client = new TossRestClient(options, new HttpClient(handler));
        handler.Respond = req =>
        {
            if (req.RequestUri!.AbsolutePath != "/oauth2/token") return FakeHandler.Json("""{"result":[]}""");
            return req.Headers.Authorization?.Scheme == "Basic"
                ? FakeHandler.Json("""{"access_token":"tok-basic","expires_in":3600}""")
                : FakeHandler.Json("""{"error":"invalid_client"}""", HttpStatusCode.Unauthorized);
        };

        var token = await client.Tokens.GetTokenAsync(TestContext.Current.CancellationToken);
        Assert.Equal("tok-basic", token);
        Assert.Equal("grant_type=client_credentials&client_id=id&client_secret=secret", handler.Requests[0].Body);
        var basic = handler.Requests[1].Request.Headers.Authorization!;
        Assert.Equal("id:secret", Encoding.UTF8.GetString(Convert.FromBase64String(basic.Parameter!)));
        Assert.Equal("grant_type=client_credentials", handler.Requests[1].Body);
    }

    [Fact]
    public async Task LatestSessionBarsKeepOnlyMostRecentTradingDay()
    {
        var (client, handler) = Create();
        handler.Respond = req => req.RequestUri!.AbsolutePath == "/oauth2/token"
            ? FakeHandler.Json("""{"access_token":"t","expires_in":3600}""")
            : FakeHandler.Json("""
                {"result":{"candles":[
                  {"timestamp":"2026-09-25T15:29:00+09:00","openPrice":"100","highPrice":"101","lowPrice":"99","closePrice":"100","volume":"5","currency":"KRW"},
                  {"timestamp":"2026-09-25T15:28:00+09:00","openPrice":"100","highPrice":"101","lowPrice":"99","closePrice":"100","volume":"5","currency":"KRW"},
                  {"timestamp":"2026-09-24T15:29:00+09:00","openPrice":"90","highPrice":"91","lowPrice":"89","closePrice":"90","volume":"5","currency":"KRW"}],
                 "nextBefore":"2026-09-24T15:28:00+09:00"}}
                """);
        var source = new TossMarketDataSource(client);
        var bars = await source.GetLatestSessionMinuteBarsAsync("005930", TestContext.Current.CancellationToken);
        Assert.Equal(2, bars.Count);
        Assert.All(bars, b => Assert.Equal(new DateOnly(2026, 9, 25), Kst.DateOf(b.Start)));
        Assert.True(bars[0].Start < bars[1].Start);
    }

    [Fact]
    public async Task HoldingsAndOrdersMapToDomain()
    {
        var (client, handler) = Create();
        handler.Respond = req => req.RequestUri!.AbsolutePath switch
        {
            "/oauth2/token" => FakeHandler.Json(Token),
            "/api/v1/orders/O1" => FakeHandler.Json("""
                {"result":{"orderId":"O1","symbol":"005930","side":"BUY","orderType":"LIMIT","timeInForce":"DAY","status":"PARTIAL_FILLED",
                 "price":"70000","quantity":"10","orderAmount":null,"currency":"KRW","orderedAt":"2026-03-28T09:30:00+09:00","canceledAt":null,
                 "execution":{"filledQuantity":"4","averageFilledPrice":"70000","filledAmount":"280000","commission":null,"tax":null,"filledAt":null,"settlementDate":null}}}
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var dto = await client.GetOrderAsync("O1", TestContext.Current.CancellationToken);
        var u = TossMapper.ToUpdate(dto);
        Assert.Equal(OrderStatus.PartialFilled, u.Status);
        Assert.Equal(4m, u.FilledQuantity);
        Assert.Equal(OrderSide.Buy, u.Side);
    }
}

public class TossStreamTests
{
    private static TossStreamClient Create(out TossOptions options)
    {
        options = new TossOptions { ClientId = "a", ClientSecret = "b" };
        return new TossStreamClient(options, new TossTokenProvider(options, new HttpClient()));
    }

    [Fact]
    public async Task DeclarationIsJsonArrayOfTypesAndCodes()
    {
        var stream = Create(out _);
        Assert.Equal("[]", stream.BuildDeclaration("1"));
        await stream.SetMarketSubscriptionsAsync(new[] { "005930", "000660", "AAPL" }, new[] { "005930" }, TestContext.Current.CancellationToken);
        await stream.SetOrderSubscriptionAsync(3, TestContext.Current.CancellationToken);
        var json = stream.BuildDeclaration("9");
        Assert.Equal(
            """[{"id":"9"},{"type":"trade:kr","codes":["000660","005930"]},{"type":"trade:us","codes":["AAPL"]},{"type":"orderbook:kr","codes":["005930"]},{"type":"personal:order","codes":["3"]}]""",
            json);
    }

    [Fact]
    public void ParsesTradeOrderbookAndOrderFrames()
    {
        var stream = Create(out _);
        TradeTick? trade = null;
        OrderBookSnapshot? book = null;
        (string Event, OrderDto Order)? order = null;
        stream.TradeReceived += t => trade = t;
        stream.OrderBookReceived += b => book = b;
        stream.OrderEventReceived += (e, o) => order = (e, o);

        stream.HandleFrame("""{"type":"message","topic":"trade:kr:005930","data":{"price":"71500","volume":"8","timestamp":"2026-06-18T10:30:00.000+09:00","currency":"KRW"}}""");
        stream.HandleFrame("""{"type":"message","topic":"orderbook:kr:005930","data":{"timestamp":"2026-06-18T10:30:00.000+09:00","currency":"KRW","asks":[{"price":"71500","volume":"5"}],"bids":[{"price":"71400","volume":"10"}]}}""");
        stream.HandleFrame("""{"type":"message","topic":"personal:order:3","data":{"event":"FILL","accountSeq":"3","order":{"orderId":"X","symbol":"005930","side":"SELL","orderType":"MARKET","timeInForce":"DAY","status":"FILLED","price":null,"quantity":"10","orderAmount":null,"currency":"KRW","orderedAt":"2026-06-23T09:30:00.000+09:00","canceledAt":null,"execution":{"filledQuantity":"10","averageFilledPrice":"71400","filledAmount":"714000","commission":"107","tax":"1428","settlementDate":"2026-06-25"}}}}""");
        stream.HandleFrame("PONG");
        stream.HandleFrame("""{"type":"subscriptions","id":"1","subscribed":["trade:kr:005930"],"rejected":[]}""");

        Assert.Equal(new TradeTick("005930", 71_500m, 8m, DateTimeOffset.Parse("2026-06-18T10:30:00+09:00")), trade);
        Assert.Equal(71_500m, book!.BestAsk);
        Assert.Equal(71_400m, book.BestBid);
        Assert.Equal("FILL", order!.Value.Event);
        var u = TossMapper.ToUpdate(order.Value.Order);
        Assert.Equal(OrderStatus.Filled, u.Status);
        Assert.Equal(OrderSide.Sell, u.Side);
        Assert.Equal(71_400m, u.AverageFilledPrice);
    }

    [Fact]
    public void MarketDetection()
    {
        Assert.Equal("kr", TossStreamClient.MarketOf("005930"));
        Assert.Equal("kr", TossStreamClient.MarketOf("00088K"));
        Assert.Equal("us", TossStreamClient.MarketOf("AAPL"));
    }
}
