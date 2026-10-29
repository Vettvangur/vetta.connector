using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using QRCoder;

namespace Vetta.Connector.AspNetCore;

public sealed class VettaDeviceLinkOptions
{
    public required Uri CallbackUrl { get; init; }
    public string ReturnPath { get; init; } = "/";
}
public sealed record DeviceLinkBrowserRequest(DeviceLinkMethod Method, string? Message = null);
public sealed record DeviceLinkReply(string State, string? Handle = null, string? QrImageUrl = null,
    string? AppLink = null, string? AndroidAppLink = null, string? ErrorCode = null);

public static class DeviceLinkEndpoints
{
    internal const string Cookie = "__Host-VettaDevice";
    public static IServiceCollection AddVettaDeviceLinks(this IServiceCollection services, VettaDeviceLinkOptions options)
    {
        if (options.CallbackUrl is not { IsAbsoluteUri: true, Scheme: "https", UserInfo.Length: 0, Fragment.Length: 0, Query.Length: 0 } || !Local(options.ReturnPath))
            throw new ArgumentException("Configure an absolute HTTPS callback without query/fragment and a local return path.");
        services.AddSingleton(options);
        services.AddScoped<VettaDeviceLinkFlow>();
        return services;
    }
    internal static void Map(RouteGroupBuilder group, IServiceProvider services, string prefix)
    {
        if (!services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(VettaDeviceLinkFlow))) return;
        var options = services.GetRequiredService<VettaDeviceLinkOptions>();
        if (options.CallbackUrl.AbsolutePath != prefix.TrimEnd('/') + "/device-link/callback")
            throw new InvalidOperationException("CallbackUrl must match the mapped device-link callback path.");
        group.MapPost("/device-link/start", async (DeviceLinkBrowserRequest request, HttpContext http, VettaDeviceLinkFlow flow) =>
        {
            var browser = Endpoints.Browser(http);
            if (browser is null) return Results.BadRequest();
            if (http.Request.Cookies.TryGetValue(Cookie, out var old) && flow.IsActive(browser, old) &&
                flow.Cancel(browser, old).State == "Busy")
                return Results.Ok(new DeviceLinkReply("Busy"));
            try
            {
                var result = await flow.StartAsync(browser, request, http.RequestAborted);
                if (result.Handle is not null && request.Method == DeviceLinkMethod.Web2App)
                    http.Response.Cookies.Append(Cookie, result.Handle, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = TimeSpan.FromMinutes(5), IsEssential = true });
                return Results.Ok(result with { QrImageUrl = result.State == "Started" && request.Method == DeviceLinkMethod.Qr ? prefix.TrimEnd('/') + "/device-link/qr?handle=" + result.Handle : null });
            }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_input" }); }
            catch (TimeoutException) { return Results.Ok(new DeviceLinkReply("Unavailable")); }
        });
        group.MapGet("/device-link/qr", async (string handle, HttpContext http, VettaDeviceLinkFlow flow) =>
        {
            var browser = Endpoints.Browser(http);
            if (browser is null || !Endpoints.Valid(handle)) return Results.BadRequest();
            var link = await flow.QrAsync(browser, handle, http.RequestAborted);
            if (link is null) return Results.NotFound();
            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
            using var code = new SvgQRCode(data);
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Text(code.GetGraphic(4), "image/svg+xml");
        });
        group.MapPost("/device-link/status", async (Endpoints.HandleRequest request, HttpContext http, VettaDeviceLinkFlow flow) =>
            Endpoints.Browser(http) is { } browser && Endpoints.Valid(request.Handle)
                ? Results.Ok(await flow.StatusAsync(http, browser, request.Handle, http.RequestAborted)) : Results.BadRequest());
        group.MapPost("/device-link/cancel", (Endpoints.HandleRequest request, HttpContext http, VettaDeviceLinkFlow flow) =>
            Endpoints.Browser(http) is { } browser && Endpoints.Valid(request.Handle)
                ? Results.Ok(flow.Cancel(browser, request.Handle)) : Results.BadRequest());
        group.MapGet("/device-link/callback", async (HttpContext http, VettaDeviceLinkFlow flow) =>
        {
            // Trust only the configured origin and the HttpOnly browser binding. Never accept an attempt ID in a callback query.
            var browser = Endpoints.Browser(http);
            var handle = http.Request.Cookies[Cookie];
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (browser is null || !Endpoints.Valid(handle) ||
                !string.Equals(http.Request.Host.Value, options.CallbackUrl.Authority, StringComparison.OrdinalIgnoreCase) ||
                http.Request.QueryString.Value?.Length > 16000)
                return Results.LocalRedirect(options.ReturnPath + (options.ReturnPath.Contains('?') ? "&" : "?") + "vettaDevice=failed");
            var result = await flow.CallbackAsync(http, browser, handle!, new Uri(options.CallbackUrl.GetLeftPart(UriPartial.Path) + http.Request.QueryString), http.RequestAborted);
            if (result.State == "Completed" && result.Redirect is not null) return Results.LocalRedirect(result.Redirect);
            return Results.LocalRedirect(options.ReturnPath + (options.ReturnPath.Contains('?') ? "&" : "?") + "vettaDevice=failed");
        });
    }
    internal static bool Local(string? url) => url is { Length: > 0 } && url[0] == '/' &&
        (url.Length == 1 || url[1] is not ('/' or '\\')) && !url.Contains('\\') && !url.Any(char.IsControl);
}

public sealed class VettaDeviceLinkFlow(IVettaDeviceLinkClient client, IVettaAttemptStore store,
    IVettaLoginCompletion completion, VettaDeviceLinkOptions options, TimeProvider time)
{
    private AttemptSnapshot? Get(string browser, string handle) => store.Read(handle, browser) is { } item && item.Attempt.Kind is "device-qr" or "device-web2app" ? item : null;
    public bool IsActive(string browser, string handle) => Get(browser, handle)?.Phase is AttemptPhase.Starting or AttemptPhase.Ready or AttemptPhase.Polling or AttemptPhase.Completing;
    public async Task<DeviceLinkReply> StartAsync(string browser, DeviceLinkBrowserRequest request, CancellationToken ct)
    {
        var handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!store.TryAdd(new(handle, browser, Guid.Empty, time.GetUtcNow().AddMinutes(5), request.Method == DeviceLinkMethod.Qr ? "device-qr" : "device-web2app"))) return new("Unavailable");
        try
        {
            var result = await client.StartAsync(new(request.Method, request.Method == DeviceLinkMethod.Web2App ? options.CallbackUrl : null, request.Message), ct);
            if (result.Authentication.State == AuthenticationState.Started && result.Authentication.Identifier is { } id && store.Bind(handle, browser, id))
                return new("Started", handle, AppLink: result.AppLink, AndroidAppLink: result.AndroidAppLink);
            store.Transition(handle, browser, AttemptPhase.Starting, AttemptPhase.Failed);
            return new("Failed", ErrorCode: result.Authentication.ErrorCode);
        }
        catch { store.Transition(handle, browser, AttemptPhase.Starting, AttemptPhase.Failed); throw; }
    }
    public async Task<string?> QrAsync(string browser, string handle, CancellationToken ct)
    {
        var entry = Get(browser, handle);
        if (entry?.Attempt.Kind != "device-qr" || entry.Phase is not (AttemptPhase.Ready or AttemptPhase.Polling)) return null;
        try { return (await client.GetQrAsync(entry.Attempt.UpstreamId, ct)).Link; }
        catch (TimeoutException) { return null; }
    }
    public Task<LoginReply> StatusAsync(HttpContext http, string browser, string handle, CancellationToken ct) => Finish(http, browser, handle, null, ct);
    public Task<LoginReply> CallbackAsync(HttpContext http, string browser, string handle, Uri url, CancellationToken ct) => Finish(http, browser, handle, url, ct);
    private async Task<LoginReply> Finish(HttpContext http, string browser, string handle, Uri? callback, CancellationToken ct)
    {
        var item = Get(browser, handle);
        if (item is null) return new("Expired");
        if (callback is not null && item.Attempt.Kind != "device-web2app") return new("Failed");
        if (item.Phase == AttemptPhase.Completed) return new("Completed", Redirect: item.Redirect);
        if (item.Phase is AttemptPhase.Cancelled or AttemptPhase.Failed) return new(item.Phase.ToString());
        // Mobile status reads local state: only the callback may consume the provider result and issue the host cookie.
        if (callback is null && item.Attempt.Kind == "device-web2app") return new("Pending");
        if (!store.Transition(handle, browser, AttemptPhase.Ready, AttemptPhase.Polling)) return new("Busy");
        try
        {
            var result = callback is null ? await client.GetStatusAsync(item.Attempt.UpstreamId, ct) : await client.CompleteCallbackAsync(item.Attempt.UpstreamId, callback, ct);
            if ((result.State == AuthenticationState.Pending && callback is null) || result.ErrorCode == "invalid_callback")
            {
                store.Transition(handle, browser, AttemptPhase.Polling, AttemptPhase.Ready);
                return new(callback is null ? "Pending" : "Failed") { ErrorCode = result.ErrorCode };
            }
            if (result.State != AuthenticationState.Succeeded || result.Identity is null)
            {
                store.Transition(handle, browser, AttemptPhase.Polling, AttemptPhase.Failed);
                return new("Failed") { ErrorCode = result.ErrorCode };
            }
            if (!store.Transition(handle, browser, AttemptPhase.Polling, AttemptPhase.Completing)) return new("Cancelled");
            var redirect = await completion.CompleteAsync(http, result.Identity, handle, ct);
            if (!DeviceLinkEndpoints.Local(redirect)) throw new InvalidOperationException("Completion must return a local URL.");
            return store.Transition(handle, browser, AttemptPhase.Completing, AttemptPhase.Completed, redirect) ? new("Completed", Redirect: redirect) : new("Expired");
        }
        catch (TimeoutException) { return new("Unavailable"); }
        finally
        {
            // Never replay a callback or host completion whose result may have been lost.
            store.Transition(handle, browser, AttemptPhase.Polling, callback is null ? AttemptPhase.Ready : AttemptPhase.Failed);
            store.Transition(handle, browser, AttemptPhase.Completing, AttemptPhase.Failed);
        }
    }
    public LoginReply Cancel(string browser, string handle)
    {
        var item = Get(browser, handle);
        if (item is null) return new("Expired");
        if (item.Phase == AttemptPhase.Cancelled) return new("Cancelled");
        return store.Transition(handle, browser, AttemptPhase.Ready, AttemptPhase.Cancelled) || store.Transition(handle, browser, AttemptPhase.Polling, AttemptPhase.Cancelled) ? new("Cancelled") : new("Busy");
    }
}
