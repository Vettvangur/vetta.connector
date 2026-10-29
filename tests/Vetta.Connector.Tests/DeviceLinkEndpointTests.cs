using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Vetta.Connector.AspNetCore;
namespace Vetta.Connector.Tests;

public sealed class DeviceLinkEndpointTests
{
    [Fact]
    public async Task Mobile_callback_requires_bound_cookies_and_start_requires_antiforgery()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Services.AddVettaConnector(options => { options.BaseUrl = new("https://example.com/"); options.ApplicationId = "test"; options.ApiKey = "fixture"; });
        var fake = new DeviceLinkTests.FakeDevice();
        builder.Services.AddSingleton<IVettaDeviceLinkClient>(fake);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddVettaAuthenticationFlow<DeviceLinkTests.Completion>();
        builder.Services.AddVettaDeviceLinks(new() { CallbackUrl = new("https://localhost/auth/vetta/device-link/callback"), ReturnPath = "/login" });
        await using var app = builder.Build(); app.MapVettaAuthentication(); await app.StartAsync();
        using var http = app.GetTestClient(); http.BaseAddress = new("https://localhost");
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("/auth/vetta/device-link/start", new { method = 1 })).StatusCode);
        var session = await http.GetAsync("/auth/vetta/session");
        var token = (await session.Content.ReadFromJsonAsync<FlowEndpointTests.SessionToken>())!.Token;
        var cookies = string.Join("; ", session.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0]));
        Assert.Contains(session.Headers.GetValues("Set-Cookie"), x => x.StartsWith("__Host-VettaBrowser") && x.Contains("samesite=lax"));
        http.DefaultRequestHeaders.Add("Cookie", cookies); http.DefaultRequestHeaders.Add("X-Vetta-CSRF", token);
        var start = await http.PostAsJsonAsync("/auth/vetta/device-link/start", new { method = 1 });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var attempt = (await start.Content.ReadFromJsonAsync<DeviceLinkReply>())!;
        var mobileCookie = start.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-VettaDevice"));
        Assert.Contains("httponly", mobileCookie); Assert.Contains("secure", mobileCookie);
        Assert.DoesNotContain("1234567890", await start.Content.ReadAsStringAsync());
        using var other = app.GetTestClient(); other.BaseAddress = http.BaseAddress;
        var rejected = await other.GetAsync("/auth/vetta/device-link/callback?identifier=" + attempt.Handle);
        Assert.Equal("/login?vettaDevice=failed", rejected.Headers.Location!.OriginalString);
        Assert.Equal(0, fake.CallbackCalls);
        http.DefaultRequestHeaders.Remove("Cookie"); http.DefaultRequestHeaders.Add("Cookie", cookies + "; " + mobileCookie.Split(';')[0]);
        var callback = await http.GetAsync("/auth/vetta/device-link/callback?state=fixture");
        Assert.Equal("/account", callback.Headers.Location!.OriginalString);
        Assert.Equal("no-referrer", callback.Headers.GetValues("Referrer-Policy").Single());
        await http.GetAsync("/auth/vetta/device-link/callback?state=fixture");
        Assert.Equal(1, fake.CallbackCalls);
        var qrStart = await http.PostAsJsonAsync("/auth/vetta/device-link/start", new { method = 0 });
        var qr = (await qrStart.Content.ReadFromJsonAsync<DeviceLinkReply>())!;
        var image = await http.GetAsync(qr.QrImageUrl);
        Assert.Equal("image/svg+xml", image.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<svg", await image.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await other.GetAsync(qr.QrImageUrl)).StatusCode);
    }
}
