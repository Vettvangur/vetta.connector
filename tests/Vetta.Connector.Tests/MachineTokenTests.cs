using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vetta.Connector;
using Vetta.Connector.AspNetCore;

public sealed class MachineTokenTests
{
    private const string Secret = "vms_super-secret_value";

    private static VettaMachineTokenOptions Options(params string[] scopes)
    {
        var options = new VettaMachineTokenOptions { Authority = new("https://identity.example/base"), ClientId = "m2m_abcdefghijklmnopqrst", ClientSecret = Secret };
        foreach (var scope in scopes) options.Scopes.Add(scope);
        return options;
    }

    private static HttpResponseMessage Token(string token, int expiresIn = 600) => new(HttpStatusCode.OK)
    { Content = new StringContent($$"""{"access_token":"{{token}}","token_type":"Bearer","expires_in":{{expiresIn}}}""", Encoding.UTF8, "application/json") };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        public HttpRequestMessage? Last; public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls); Last = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return await send(request);
        }
    }

    [Fact]
    public async Task Requests_client_credentials_with_basic_auth_and_caches_until_shortly_before_expiry()
    {
        var clock = new Clock(); var issued = 0;
        var handler = new Handler(_ => Task.FromResult(Token("token-" + ++issued)));
        using var provider = new VettaMachineTokenProvider(() => new HttpClient(handler), Options("orders.read", "orders.write"), clock);

        Assert.Equal("token-1", await provider.GetTokenAsync());
        Assert.Equal(new Uri("https://identity.example/base/connect/token"), handler.Last!.RequestUri);
        Assert.Equal(HttpMethod.Post, handler.Last.Method);
        Assert.Equal("Basic", handler.Last.Headers.Authorization!.Scheme);
        Assert.Equal("m2m_abcdefghijklmnopqrst:" + Secret, Encoding.UTF8.GetString(Convert.FromBase64String(handler.Last.Headers.Authorization.Parameter!)));
        Assert.Equal("grant_type=client_credentials&scope=orders.read+orders.write", handler.Body);

        clock.Now = clock.Now.AddSeconds(539);
        Assert.Equal("token-1", await provider.GetTokenAsync());
        clock.Now = clock.Now.AddSeconds(1); // 60 seconds before expiry
        Assert.Equal("token-2", await provider.GetTokenAsync());
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Omitted_scopes_request_every_permitted_scope_and_short_tokens_refresh_at_half_life()
    {
        var clock = new Clock(); var issued = 0;
        var handler = new Handler(_ => Task.FromResult(Token("t" + ++issued, expiresIn: 60)));
        using var provider = new VettaMachineTokenProvider(() => new HttpClient(handler), Options(), clock);
        await provider.GetTokenAsync();
        Assert.Equal("grant_type=client_credentials", handler.Body);
        clock.Now = clock.Now.AddSeconds(29); Assert.Equal("t1", await provider.GetTokenAsync());
        clock.Now = clock.Now.AddSeconds(1); Assert.Equal("t2", await provider.GetTokenAsync());
    }

    [Fact]
    public async Task Concurrent_callers_share_one_refresh()
    {
        var release = new TaskCompletionSource();
        var handler = new Handler(async _ => { await release.Task; return Token("shared"); });
        using var provider = new VettaMachineTokenProvider(() => new HttpClient(handler), Options(), new Clock());
        var calls = Enumerable.Range(0, 20).Select(_ => provider.GetTokenAsync().AsTask()).ToArray();
        await Task.Delay(50); release.SetResult();
        Assert.All(await Task.WhenAll(calls), token => Assert.Equal("shared", token));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"invalid_client","error_description":"bad"}""", "invalid_client")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_scope"}""", "invalid_scope")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"<script>"}""", null)]
    [InlineData(HttpStatusCode.InternalServerError, "oops", null)]
    public async Task Refusals_report_the_oauth_error_without_the_secret(HttpStatusCode status, string body, string? error)
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));
        using var provider = new VettaMachineTokenProvider(() => new HttpClient(handler), Options(), new Clock());
        var exception = await Assert.ThrowsAsync<VettaMachineTokenException>(() => provider.GetTokenAsync().AsTask());
        Assert.Equal(error, exception.Error); Assert.Equal(status, exception.StatusCode);
        Assert.DoesNotContain(Secret, exception.ToString());
    }

    [Theory]
    [InlineData("""{"access_token":"x","token_type":"mac","expires_in":600}""")]
    [InlineData("""{"access_token":"","token_type":"Bearer","expires_in":600}""")]
    [InlineData("""{"access_token":"x","token_type":"Bearer","expires_in":0}""")]
    [InlineData("not json")]
    public async Task Malformed_token_responses_are_rejected(string body)
    {
        var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }));
        using var provider = new VettaMachineTokenProvider(() => new HttpClient(handler), Options(), new Clock());
        await Assert.ThrowsAsync<VettaMachineTokenException>(() => provider.GetTokenAsync().AsTask());
    }

    [Fact]
    public async Task Oversized_responses_and_timeouts_are_reported()
    {
        var big = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 70000)) }));
        using (var provider = new VettaMachineTokenProvider(() => new HttpClient(big), Options(), new Clock()))
            await Assert.ThrowsAsync<VettaMachineTokenException>(() => provider.GetTokenAsync().AsTask());
        var options = Options(); options.RequestTimeout = TimeSpan.FromMilliseconds(50);
        var stalled = new HttpClient(new StallHandler());
        using var timed = new VettaMachineTokenProvider(() => stalled, options, new Clock());
        await Assert.ThrowsAsync<TimeoutException>(() => timed.GetTokenAsync().AsTask());
    }

    private sealed class StallHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return new(HttpStatusCode.OK); }
    }

    [Theory]
    [InlineData("http://identity.example/", "id", "secret")]
    [InlineData("https://user@identity.example/", "id", "secret")]
    [InlineData("https://identity.example/?x=1", "id", "secret")]
    [InlineData("https://identity.example/", "", "secret")]
    [InlineData("https://identity.example/", "id", " ")]
    public void Invalid_options_are_rejected_before_any_request(string authority, string clientId, string secret)
    {
        var options = new VettaMachineTokenOptions { Authority = new(authority), ClientId = clientId, ClientSecret = secret };
        Assert.Throws<ArgumentException>(() => new VettaMachineTokenProvider(() => new HttpClient(), options));
        var withSpace = Options("orders read");
        Assert.Throws<ArgumentException>(() => new VettaMachineTokenProvider(() => new HttpClient(), withSpace));
    }

    [Fact]
    public async Task Http_client_registration_adds_a_bearer_token_from_its_own_cache()
    {
        var tokenHandler = new Handler(request => Task.FromResult(Token("issued-" + request.RequestUri!.Host)));
        var api = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var services = new ServiceCollection();
        services.AddHttpClient("orders").ConfigurePrimaryHttpMessageHandler(() => api)
            .AddVettaMachineToken(o => { o.Authority = new("https://identity.example/"); o.ClientId = "m2m_abcdefghijklmnopqrst"; o.ClientSecret = Secret; });
        services.AddHttpClient("Vetta.MachineToken:orders").ConfigurePrimaryHttpMessageHandler(() => tokenHandler);
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("orders");
        await client.GetAsync("https://orders.example/api");
        await client.GetAsync("https://orders.example/api");
        Assert.Equal("Bearer", api.Last!.Headers.Authorization!.Scheme);
        Assert.Equal("issued-identity.example", api.Last.Headers.Authorization.Parameter);
        Assert.Equal(1, tokenHandler.Calls);
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddHttpClient("bad").AddVettaMachineToken(o => o.ClientId = "x"));
    }

    [Fact]
    public void Api_registration_validates_audience_issuer_and_lifetime_with_raw_claims()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddAuthentication().AddVettaApi("vetta", o => { o.Authority = "https://identity.example/"; o.Audience = "https://api.customer.example"; });
        using var provider = services.BuildServiceProvider();
        var jwt = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("vetta");
        Assert.Equal("https://identity.example/", jwt.Authority); Assert.Equal("https://api.customer.example", jwt.Audience);
        Assert.True(jwt.RequireHttpsMetadata); Assert.False(jwt.MapInboundClaims); Assert.False(jwt.SaveToken);
        var p = jwt.TokenValidationParameters;
        Assert.True(p.ValidateIssuer && p.ValidateAudience && p.ValidateLifetime && p.RequireExpirationTime && p.RequireSignedTokens);
        Assert.Equal("https://api.customer.example", p.ValidAudience); Assert.Equal(TimeSpan.FromSeconds(30), p.ClockSkew);
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddAuthentication().AddVettaApi("x", o => { o.Authority = "http://identity.example/"; o.Audience = "a"; }));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddAuthentication().AddVettaApi("x", o => { o.Authority = "https://identity.example/"; }));
    }

    [Theory]
    [InlineData("orders.read orders.write", new[] { "orders.read" }, true)]
    [InlineData("orders.read orders.write", new[] { "orders.read", "orders.write" }, true)]
    [InlineData("orders.read", new[] { "orders.write" }, false)]
    [InlineData("orders.readonly", new[] { "orders.read" }, false)]
    public async Task Scope_policy_requires_every_listed_scope(string granted, string[] required, bool allowed)
    {
        var policy = new AuthorizationPolicyBuilder().RequireVettaScope(required).Build();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("scope", granted)], "Bearer"));
        var services = new ServiceCollection(); services.AddLogging(); services.AddAuthorization();
        using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, null, policy);
        Assert.Equal(allowed, result.Succeeded);
        Assert.Throws<ArgumentException>(() => new AuthorizationPolicyBuilder().RequireVettaScope());
        Assert.Throws<ArgumentException>(() => new AuthorizationPolicyBuilder().RequireVettaScope("a b"));
    }
}
