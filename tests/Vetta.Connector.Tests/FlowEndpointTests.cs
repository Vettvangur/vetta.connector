using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Vetta.Connector.AspNetCore;

namespace Vetta.Connector.Tests;

public sealed class FlowEndpointTests
{
    [Fact]
    public async Task Endpoints_require_csrf_and_bound_browser_and_return_no_identity()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        var client = new FakeClient();
        builder.Services.AddSingleton<IVettaClient>(client);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddVettaAuthenticationFlow<Completion>();
        await using var app = builder.Build();
        app.MapVettaAuthentication();
        await app.StartAsync();
        using var http = app.GetTestClient();
        http.BaseAddress = new("https://localhost");
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/auth/vetta/start", new StartRequest(AuthenticationMethod.App, "1234567890"))).StatusCode);
        Assert.Equal(0, client.Starts);
        var session = await http.GetAsync("/auth/vetta/session");
        Assert.Contains("no-store", session.Headers.CacheControl!.ToString());
        var token = (await session.Content.ReadFromJsonAsync<SessionToken>())!.Token;
        var cookies = string.Join("; ", session.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0]));
        http.DefaultRequestHeaders.Add("Cookie", cookies);
        http.DefaultRequestHeaders.Add("X-Vetta-CSRF", token);
        var started = await http.PostAsJsonAsync("/auth/vetta/start", new StartRequest(AuthenticationMethod.App, "1234567890"));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var login = (await started.Content.ReadFromJsonAsync<LoginReply>())!;
        Assert.Equal("Started", login.State);
        var result = await http.PostAsJsonAsync("/auth/vetta/status", new { handle = login.Handle });
        Assert.Equal("Completed", (await result.Content.ReadFromJsonAsync<LoginReply>())!.State);
        Assert.DoesNotContain("1234567890", await result.Content.ReadAsStringAsync());
        Assert.Equal(1, client.Starts);
        Assert.Contains("export class VettaLogin", await http.GetStringAsync("/auth/vetta/client.js"));
    }

    [Fact]
    public async Task Development_store_cannot_be_used_in_production()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddVettaAuthenticationFlow<Completion>();
        await using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.MapVettaAuthentication());
    }
    public sealed record SessionToken(string Token);
    private sealed class Completion : IVettaLoginCompletion
    {
        public Task<string> CompleteAsync(HttpContext context, VerifiedIdentity identity, string id, CancellationToken ct) => Task.FromResult("/account");
    }
    private sealed class FakeClient : IVettaClient
    {
        public int Starts;
        public Task<AuthenticationResult> StartAsync(StartRequest request, CancellationToken ct = default)
        { Starts++; return Task.FromResult(new AuthenticationResult(AuthenticationState.Started, Guid.NewGuid(), "1234")); }
        public Task<AuthenticationResult> WaitForStatusAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(new AuthenticationResult(AuthenticationState.Succeeded, Identity: new("Fixture", "1234567890")));
        public Task<AuthenticationResult> GetStatusAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AuthenticationResult> AuthenticateSimpleAsync(AuthenticationMethod method, string input, string? message = null, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
