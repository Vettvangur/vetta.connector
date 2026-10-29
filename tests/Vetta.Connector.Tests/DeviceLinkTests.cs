using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Vetta.Connector.AspNetCore;

namespace Vetta.Connector.Tests;

public sealed class DeviceLinkTests
{
    [Theory]
    [InlineData(DeviceLinkMethod.Qr, "QR", "qrLink")]
    [InlineData(DeviceLinkMethod.Web2App, "Web2App", "appLink")]
    public async Task Starts_use_correct_contract_and_only_validated_presentation(DeviceLinkMethod method, string flow, string link)
    {
        var id = Guid.NewGuid();
        var handler = new Handler(async request => {
            Assert.Equal("/devicelink/start", request.RequestUri!.AbsolutePath);
            Assert.Equal("apikey", request.Headers.Authorization!.Scheme);
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal(flow, body.GetProperty("flow").GetString());
            Assert.Equal("test", body.GetProperty("requestingApplication").GetString());
            return Response($$"""{"identifier":"{{id}}","flow":"{{flow}}","{{link}}":"https://www.audkenniapp.is/device-link?token=fixture","androidAppLink":"javascript:unsafe","qrToken":"secret"}""");
        });
        var result = await Client(handler).StartAsync(new(method, new Uri("https://customer.example/callback")));
        Assert.Equal(AuthenticationState.Started, result.Authentication.State);
        Assert.DoesNotContain("unsafe", result.ToString()); Assert.DoesNotContain("secret", result.ToString());
        if (method == DeviceLinkMethod.Web2App) Assert.Contains("package=is.audkenni.app", result.AndroidAppLink);
        Assert.Equal(1, handler.Calls);
    }
    [Theory]
    [InlineData("https://evil.example/device-link")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://www.audkenniapp.is@evil.example/device-link")]
    public async Task Untrusted_links_are_rejected(string link)
    {
        var handler = new Handler(_ => Task.FromResult(Response($$"""{"identifier":"{{Guid.NewGuid()}}","flow":"QR","qrLink":"{{link}}"}""")));
        Assert.Equal(AuthenticationError.InvalidResponse, (await Client(handler).StartAsync(new(DeviceLinkMethod.Qr))).Authentication.Error);
    }
    [Theory]
    [InlineData("user_not_found", "user_not_found")]
    [InlineData("secret database details", null)]
    public async Task Safe_error_codes_survive_http_errors_but_messages_do_not(string code, string? expected)
    {
        var handler = new Handler(_ => Task.FromResult(Response(JsonSerializer.Serialize(new { error = "sensitive", errorCode = code }), HttpStatusCode.ServiceUnavailable)));
        var result = await Client(handler).StartAsync(new(DeviceLinkMethod.Qr));
        Assert.Equal(expected, result.Authentication.ErrorCode); Assert.Equal(503, result.Authentication.HttpStatusCode);
        Assert.DoesNotContain("sensitive", result.ToString());
        var legacy = new VettaClient(new HttpClient(handler), Options.Create(Settings()));
        Assert.Equal(expected, (await legacy.StartAsync(new(AuthenticationMethod.App, "1234567890"))).ErrorCode);
    }
    [Fact]
    public async Task Qr_fetch_uses_backend_key_and_status_callback_preserves_full_url()
    {
        var id = Guid.NewGuid(); var callback = new Uri("https://customer.example/callback?state=abc&authResumeUrl=https%3A%2F%2Fexample.test%2Fresume");
        var handler = new Handler(async request => {
            Assert.NotNull(request.Headers.Authorization);
            if (request.Method == HttpMethod.Get) { Assert.Contains(id.ToString(), request.RequestUri!.Query); return Response("""{"link":"https://www.audkenniapp.is/device-link?token=fixture"}"""); }
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
            Assert.Equal(callback.AbsoluteUri, body.GetProperty("callbackUrl").GetString());
            return Response("""{"status":"Success","finishedPendingUser":true,"userInfo":{"name":"Fixture","ssn":"1234567890"}}""");
        });
        var client = Client(handler);
        Assert.NotNull((await client.GetQrAsync(id)).Link);
        Assert.Equal(AuthenticationState.Succeeded, (await client.CompleteCallbackAsync(id, callback)).State);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"status\":\"Success\",\"finishedPendingUser\":false}")]
    public async Task Invalid_response_never_authenticates(string body)
    {
        var result = await Client(new Handler(_ => Task.FromResult(Response(body)))).GetStatusAsync(Guid.NewGuid());
        Assert.Null(result.Identity); Assert.Equal(AuthenticationError.InvalidResponse, result.Error);
    }
    [Fact]
    public async Task Callback_is_browser_bound_single_use_and_mobile_poll_does_not_consume_it()
    {
        var client = new FakeDevice(); var completion = new Completion();
        var store = new DevelopmentAttemptStore(TimeProvider.System);
        var flow = new VettaDeviceLinkFlow(client, store, completion, new() { CallbackUrl = new("https://example.com/callback") }, TimeProvider.System);
        var started = await flow.StartAsync("browser", new(DeviceLinkMethod.Web2App), default);
        Assert.Equal("Expired", (await flow.CallbackAsync(new DefaultHttpContext(), "other", started.Handle!, new("https://example.com/callback"), default)).State);
        Assert.Equal("Pending", (await flow.StatusAsync(new DefaultHttpContext(), "browser", started.Handle!, default)).State);
        Assert.Equal(0, client.StatusCalls);
        Assert.Equal(0, client.CallbackCalls);
        Assert.Equal("Completed", (await flow.CallbackAsync(new DefaultHttpContext(), "browser", started.Handle!, new("https://example.com/callback"), default)).State);
        await flow.CallbackAsync(new DefaultHttpContext(), "browser", started.Handle!, new("https://example.com/callback"), default);
        Assert.Equal(1, client.CallbackCalls); Assert.Equal(1, completion.Calls);
    }
    [Fact]
    public async Task Cancelled_qr_cannot_complete_or_refresh()
    {
        var client = new FakeDevice(); var completion = new Completion();
        var flow = new VettaDeviceLinkFlow(client, new DevelopmentAttemptStore(TimeProvider.System), completion, new() { CallbackUrl = new("https://example.com/callback") }, TimeProvider.System);
        var started = await flow.StartAsync("browser", new(DeviceLinkMethod.Qr), default);
        Assert.Equal("Cancelled", flow.Cancel("browser", started.Handle!).State);
        Assert.Null(await flow.QrAsync("browser", started.Handle!, default));
        Assert.Equal("Cancelled", (await flow.StatusAsync(new DefaultHttpContext(), "browser", started.Handle!, default)).State);
        Assert.Equal(0, completion.Calls);
    }
    private static VettaConnectorOptions Settings() => new() { BaseUrl = new("https://example.com/"), ApplicationId = "test", ApiKey = "fixture-key" };
    private static VettaDeviceLinkClient Client(Handler handler) => new(new HttpClient(handler), Options.Create(Settings()));
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return send(request); }
    }
    internal sealed class FakeDevice : IVettaDeviceLinkClient
    {
        public int StatusCalls, CallbackCalls;
        public Task<DeviceLinkStartResult> StartAsync(DeviceLinkStartRequest request, CancellationToken ct = default) => Task.FromResult(new DeviceLinkStartResult(new(AuthenticationState.Started, Guid.NewGuid()), request.Method, AppLink: "https://www.audkenniapp.is/device-link"));
        public Task<DeviceLinkQrResult> GetQrAsync(Guid id, CancellationToken ct = default) => Task.FromResult(new DeviceLinkQrResult("https://www.audkenniapp.is/device-link"));
        public Task<AuthenticationResult> GetStatusAsync(Guid id, CancellationToken ct = default) { StatusCalls++; return Task.FromResult(new AuthenticationResult(AuthenticationState.Succeeded, id, Identity: new("Fixture", "1234567890"))); }
        public Task<AuthenticationResult> WaitForStatusAsync(Guid id, CancellationToken ct = default) => GetStatusAsync(id, ct);
        public Task<AuthenticationResult> CompleteCallbackAsync(Guid id, Uri url, CancellationToken ct = default) { CallbackCalls++; return GetStatusAsync(id, ct); }
    }
    internal sealed class Completion : IVettaLoginCompletion
    {
        public int Calls;
        public Task<string> CompleteAsync(HttpContext context, VerifiedIdentity identity, string completionId, CancellationToken ct) { Calls++; return Task.FromResult("/account"); }
    }
}
