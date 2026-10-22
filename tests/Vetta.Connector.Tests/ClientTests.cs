using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Vetta.Connector.Tests;

public sealed class ClientTests
{
    private static VettaConnectorOptions Settings() => new()
    { BaseUrl = new("https://localhost/"), ApplicationId = "fixture", ApiKey = "fixture-key" };

    [Theory]
    [InlineData(AuthenticationMethod.Sim, "+354 555-0100", "5550100", "sim")]
    [InlineData(AuthenticationMethod.App, "123456-7890", "1234567890", "app")]
    public async Task Start_sends_configured_identity_and_returns_challenge(AuthenticationMethod method, string input, string normalized, string wireMethod)
    {
        var id = Guid.NewGuid();
        var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal("/authorize-code", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("apiKey fixture-key", request.Headers.Authorization!.ToString());
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("fixture", json.RootElement.GetProperty("requestingApplication").GetString());
            Assert.Equal(normalized, json.RootElement.GetProperty("phoneNumberOrSsn").GetString());
            Assert.Equal(wireMethod, json.RootElement.GetProperty("authType").GetString());
            return Response($$"""{"identifier":"{{id}}","challenge":"1234"}""");
        });
        var result = await Client(handler).StartAsync(new(method, input));
        Assert.Equal(AuthenticationState.Started, result.State);
        Assert.Equal(id, result.Identifier);
        Assert.Equal("1234", result.Challenge);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"status\":\"Success\",\"finishedPendingUser\":true}")]
    [InlineData("{\"status\":\"Unknown\",\"finishedPendingUser\":true}")]
    [InlineData("{\"status\":\"Success\",\"finishedPendingUser\":false}")]
    public async Task Malformed_status_never_authenticates(string json)
    {
        var result = await Client(new((_, _) => Task.FromResult(Response(json)))).GetStatusAsync(Guid.NewGuid());
        Assert.Equal(AuthenticationError.InvalidResponse, result.Error);
        Assert.Null(result.Identity);
    }

    [Fact]
    public async Task Pending_does_not_trigger_another_request()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response("""{"status":"Pending","finishedPendingUser":false}""")));
        var result = await Client(handler).WaitForStatusAsync(Guid.NewGuid());
        Assert.Equal(AuthenticationState.Pending, result.State);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Explicit_success_returns_verified_identity()
    {
        var result = await Client(new((_, _) => Task.FromResult(Response("""{"status":"Success","finishedPendingUser":true,"userInfo":{"name":"Fixture","ssn":"1234567890"}}""")))).GetStatusAsync(Guid.NewGuid());
        Assert.Equal(new VerifiedIdentity("Fixture", "1234567890"), result.Identity);
    }

    [Fact]
    public async Task Oversized_response_is_rejected()
    {
        var result = await Client(new((_, _) => Task.FromResult(Response(new string(' ', 65537))))).GetStatusAsync(Guid.NewGuid());
        Assert.Equal(AuthenticationError.InvalidResponse, result.Error);
    }

    [Fact]
    public async Task Transport_failure_is_not_retried()
    {
        var handler = new Handler((_, _) => throw new HttpRequestException("sensitive transport details"));
        var result = await Client(handler).StartAsync(new(AuthenticationMethod.Sim, "5550100"));
        Assert.Equal(AuthenticationError.TransportFailure, result.Error);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("sensitive", result.ToString());
    }

    [Fact]
    public async Task Raw_error_body_is_not_exposed()
    {
        var result = await Client(new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        { Content = new StringContent("secret payload") }))).GetStatusAsync(Guid.NewGuid());
        Assert.Equal(AuthenticationError.Unauthorized, result.Error);
        Assert.DoesNotContain("secret payload", result.ToString());
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_timeout_is_distinct()
    {
        var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Response("{}"); });
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).GetStatusAsync(Guid.NewGuid(), caller.Token));
        Assert.Equal(0, handler.Calls);
        var settings = Settings(); settings.RequestTimeout = TimeSpan.FromMilliseconds(20);
        var client = new VettaClient(new HttpClient(handler), Options.Create(settings));
        await Assert.ThrowsAsync<TimeoutException>(() => client.GetStatusAsync(Guid.NewGuid()));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Invalid_server_url_is_rejected_before_sending_credentials()
    {
        var options = Settings(); options.BaseUrl = new("http://example.com/");
        Assert.Throws<ArgumentException>(() => new VettaClient(new HttpClient(), Options.Create(options)));
    }

    private static VettaClient Client(Handler handler) => new(new HttpClient(handler), Options.Create(Settings()));
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return send(request, cancellationToken); }
    }
}
