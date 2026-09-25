using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TossTrading.Domain;

namespace TossTrading.Toss;

/// <summary>토스 API 오류. 토스 에러 코드 예: invalid-token, insufficient-buying-power, rate-limit-exceeded</summary>
public sealed class TossApiException : BrokerException
{
    public TossApiException(HttpStatusCode status, string code, string message, string? requestId, TimeSpan? retryAfter)
        : base(code, $"[{(int)status} {code}] {message}", IsTransientStatus(status), retryAfter)
    {
        StatusCode = status;
        RequestId = requestId;
    }

    public HttpStatusCode StatusCode { get; }
    public string? RequestId { get; }

    private static bool IsTransientStatus(HttpStatusCode s) => s == HttpStatusCode.TooManyRequests || (int)s >= 500;
}

/// <summary>OAuth2 Client Credentials 토큰 관리. 만료 60초 전 선제 갱신, 동시 요청 시 한 번만 발급.</summary>
public sealed class TossTokenProvider
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private readonly TossOptions _options;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public TossTokenProvider(TossOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
    }

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt - RefreshMargin) return _token;

            var issuedAt = DateTimeOffset.UtcNow;
            using var req = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl.TrimEnd('/') + "/oauth2/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret,
                }),
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            TokenResponse? tr = null;
            try { tr = JsonSerializer.Deserialize<TokenResponse>(body, TossJson.Options); } catch (JsonException) { }

            if (!resp.IsSuccessStatusCode || tr?.AccessToken is null)
            {
                var code = tr?.Error ?? "token-error";
                var desc = tr?.ErrorDescription ?? Truncate(body);
                if (resp.StatusCode == HttpStatusCode.Forbidden)
                    desc += " (허용 IP 미등록 가능성: 토스증권 WTS → 설정 → Open API → 허용 IP 관리)";
                throw new TossApiException(resp.StatusCode, code, desc, null, null);
            }

            _token = tr.AccessToken;
            _expiresAt = issuedAt.AddSeconds(tr.ExpiresIn > 0 ? tr.ExpiresIn : 3600);
            return _token;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public void Invalidate(string stale)
    {
        if (_token == stale) { _token = null; _expiresAt = default; }
    }

    internal static string Truncate(string s) => s.Length <= 200 ? s : s[..200];
}

/// <summary>
/// 토스증권 Open API REST 클라이언트.
/// - Bearer 토큰 자동 발급/갱신, 401(expired/invalid-token) 시 1회 재시도 (GET·멱등 요청만)
/// - 그룹별 호출 한도(RateGate), 429 시 Retry-After 대기 후 재시도
/// - 계좌 API 는 X-Tossinvest-Account 헤더
/// </summary>
public sealed class TossRestClient : IDisposable
{
    private readonly TossOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Dictionary<RateGroup, RateGate> _gates;
    private readonly Dictionary<RateGroup, int> _serverLimits = new();

    public TossRestClient(TossOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = options.HttpTimeout };
        Tokens = new TossTokenProvider(options, _http);
        _gates = new Dictionary<RateGroup, RateGate>
        {
            [RateGroup.Auth] = Gate(RateGroup.Auth, () => options.AuthPerSecond),
            [RateGroup.MarketData] = Gate(RateGroup.MarketData, () => options.MarketDataPerSecond),
            [RateGroup.Stock] = Gate(RateGroup.Stock, () => options.StockPerSecond),
            [RateGroup.Account] = Gate(RateGroup.Account, () => options.AccountPerSecond),
            [RateGroup.Asset] = Gate(RateGroup.Asset, () => options.AssetPerSecond),
            // 주문 생성/정정/취소는 엔진 OrderManager 가 별도로 한도 관리 → 여기선 넉넉히
            [RateGroup.Order] = Gate(RateGroup.Order, () => 20),
            [RateGroup.OrderQuery] = Gate(RateGroup.OrderQuery, () => options.OrderQueryPerSecond),
        };
    }

    public TossTokenProvider Tokens { get; }
    public TossOptions Options => _options;

    private RateGate Gate(RateGroup g, Func<int> configured) =>
        new(() => _serverLimits.TryGetValue(g, out var s) && s > 0 ? Math.Min(s, configured()) : configured());

    // ================================================================ 조회

    public Task<List<AccountDto>> GetAccountsAsync(CancellationToken ct) =>
        GetAsync<List<AccountDto>>("/api/v1/accounts", null, RateGroup.Account, account: false, ct);

    public Task<List<PriceDto>> GetPricesAsync(IEnumerable<string> symbols, CancellationToken ct) =>
        GetAsync<List<PriceDto>>("/api/v1/prices", new() { ["symbols"] = string.Join(",", symbols) }, RateGroup.MarketData, false, ct);

    public Task<OrderbookDto> GetOrderbookAsync(string symbol, CancellationToken ct) =>
        GetAsync<OrderbookDto>("/api/v1/orderbook", new() { ["symbol"] = symbol }, RateGroup.MarketData, false, ct);

    public Task<CandlePageDto> GetCandlesAsync(string symbol, string interval, int count, DateTimeOffset? before, CancellationToken ct)
    {
        var q = new Dictionary<string, string?> { ["symbol"] = symbol, ["interval"] = interval, ["count"] = count.ToString(CultureInfo.InvariantCulture) };
        if (before is { } b) q["before"] = b.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);
        return GetAsync<CandlePageDto>("/api/v1/candles", q, RateGroup.MarketData, false, ct);
    }

    public Task<PriceLimitsDto> GetPriceLimitsAsync(string symbol, CancellationToken ct) =>
        GetAsync<PriceLimitsDto>("/api/v1/price-limits", new() { ["symbol"] = symbol }, RateGroup.MarketData, false, ct);

    /// <param name="type">MARKET_TRADING_AMOUNT / MARKET_TRADING_VOLUME / TOP_GAINERS ...</param>
    /// <param name="duration">realtime / 1d ... (TOP_GAINERS 는 realtime 미지원)</param>
    public Task<RankingsDto> GetRankingsAsync(string type, string marketCountry, string duration, int count, bool? excludeCaution, CancellationToken ct)
    {
        var q = new Dictionary<string, string?>
        {
            ["type"] = type, ["marketCountry"] = marketCountry, ["duration"] = duration,
            ["count"] = count.ToString(CultureInfo.InvariantCulture),
        };
        if (excludeCaution is { } ex) q["excludeInvestmentCaution"] = ex ? "true" : "false";
        return GetAsync<RankingsDto>("/api/v1/rankings", q, RateGroup.MarketData, false, ct);
    }

    public Task<List<StockDto>> GetStocksAsync(IEnumerable<string> symbols, CancellationToken ct) =>
        GetAsync<List<StockDto>>("/api/v1/stocks", new() { ["symbols"] = string.Join(",", symbols) }, RateGroup.Stock, false, ct);

    public Task<List<WarningDto>> GetWarningsAsync(string symbol, CancellationToken ct) =>
        GetAsync<List<WarningDto>>($"/api/v1/stocks/{Uri.EscapeDataString(symbol)}/warnings", null, RateGroup.Stock, false, ct);

    public Task<HoldingsDto> GetHoldingsAsync(CancellationToken ct) =>
        GetAsync<HoldingsDto>("/api/v1/holdings", null, RateGroup.Asset, account: true, ct);

    public Task<BuyingPowerDto> GetBuyingPowerAsync(string currency, CancellationToken ct) =>
        GetAsync<BuyingPowerDto>("/api/v1/buying-power", new() { ["currency"] = currency }, RateGroup.OrderQuery, account: true, ct);

    public Task<List<CommissionDto>> GetCommissionsAsync(CancellationToken ct) =>
        GetAsync<List<CommissionDto>>("/api/v1/commissions", null, RateGroup.OrderQuery, account: true, ct);

    public Task<OrderPageDto> ListOrdersAsync(string status, CancellationToken ct) =>
        GetAsync<OrderPageDto>("/api/v1/orders", new() { ["status"] = status }, RateGroup.OrderQuery, account: true, ct);

    public Task<OrderDto> GetOrderAsync(string orderId, CancellationToken ct) =>
        GetAsync<OrderDto>($"/api/v1/orders/{Uri.EscapeDataString(orderId)}", null, RateGroup.OrderQuery, account: true, ct);

    // ================================================================ 주문

    public Task<PlaceResultDto> PlaceOrderAsync(PlaceOrderBody body, CancellationToken ct) =>
        SendAsync<PlaceResultDto>(HttpMethod.Post, "/api/v1/orders", null, body, RateGroup.Order, account: true,
            idempotent: !string.IsNullOrEmpty(body.ClientOrderId), ct);

    public Task<PlaceResultDto> ModifyOrderAsync(string orderId, ModifyOrderBody body, CancellationToken ct) =>
        SendAsync<PlaceResultDto>(HttpMethod.Post, $"/api/v1/orders/{Uri.EscapeDataString(orderId)}/modify", null, body, RateGroup.Order, true, false, ct);

    public Task<PlaceResultDto> CancelOrderAsync(string orderId, CancellationToken ct) =>
        SendAsync<PlaceResultDto>(HttpMethod.Post, $"/api/v1/orders/{Uri.EscapeDataString(orderId)}/cancel", null, new { }, RateGroup.Order, true, false, ct);

    // ================================================================ 공통

    private Task<T> GetAsync<T>(string path, Dictionary<string, string?>? query, RateGroup group, bool account, CancellationToken ct) =>
        SendAsync<T>(HttpMethod.Get, path, query, null, group, account, idempotent: true, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, Dictionary<string, string?>? query, object? body,
        RateGroup group, bool account, bool idempotent, CancellationToken ct)
    {
        if (!_options.HasCredentials) throw new TossApiException(HttpStatusCode.Unauthorized, "no-credentials", "Client ID/Secret 이 설정되지 않았습니다.", null, null);
        if (account && _options.AccountSeq <= 0) throw new TossApiException(HttpStatusCode.BadRequest, "account-header-required", "계좌(accountSeq)가 선택되지 않았습니다.", null, null);

        var url = _options.BaseUrl.TrimEnd('/') + path;
        if (query is { Count: > 0 })
            url += "?" + string.Join("&", query.Where(kv => kv.Value is not null).Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
        var payload = body is null ? null : JsonSerializer.Serialize(body, TossJson.Options);

        var tokenRetried = false;
        for (var attempt = 1; ; attempt++)
        {
            await _gates[group].WaitAsync(ct).ConfigureAwait(false);
            var token = await Tokens.GetTokenAsync(ct).ConfigureAwait(false);
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (account) req.Headers.Add("X-Tossinvest-Account", _options.AccountSeq.ToString(CultureInfo.InvariantCulture));
            if (payload is not null) req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            LearnRateLimit(group, resp);
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                if (typeof(T) == typeof(object) || string.IsNullOrWhiteSpace(text)) return default!;
                var env = JsonSerializer.Deserialize<ResultEnvelope<T>>(text, TossJson.Options);
                if (env is null || env.Result is null) throw new TossApiException(resp.StatusCode, "no-result", $"{path}: result 없음", null, null);
                return env.Result;
            }

            var err = ParseError(resp, text);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && err.Code is "expired-token" or "invalid-token")
            {
                Tokens.Invalidate(token);
                if (!tokenRetried && idempotent) { tokenRetried = true; continue; }
            }
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && idempotent && attempt < 3)
            {
                await Task.Delay(err.RetryAfter ?? TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                continue;
            }
            throw err;
        }
    }

    private void LearnRateLimit(RateGroup group, HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("X-RateLimit-Limit", out var values)
            && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) && limit > 0)
            _serverLimits[group] = limit;
    }

    private static TossApiException ParseError(HttpResponseMessage resp, string text)
    {
        TimeSpan? retryAfter = null;
        if (resp.Headers.RetryAfter is { } ra)
            retryAfter = ra.Delta ?? (ra.Date is { } d ? d - DateTimeOffset.UtcNow : null);

        string code = "http-" + (int)resp.StatusCode, message = TossTokenProvider.Truncate(text);
        string? requestId = resp.Headers.TryGetValues("X-Request-Id", out var ids) ? ids.FirstOrDefault() : null;
        try
        {
            var env = JsonSerializer.Deserialize<ErrorEnvelope>(text, TossJson.Options);
            if (env?.Error is { } e)
            {
                code = e.Code ?? code;
                message = e.Message ?? message;
                requestId = e.RequestId ?? requestId;
            }
        }
        catch (JsonException) { }
        if (resp.StatusCode == HttpStatusCode.Forbidden) message += " (허용 IP 등록 여부 확인)";
        return new TossApiException(resp.StatusCode, code, message, requestId, retryAfter);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
