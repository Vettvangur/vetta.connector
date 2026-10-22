using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Vetta.Connector.AspNetCore;

public static class Endpoints
{
    private const string BrowserCookie = "__Host-VettaBrowser";
    public static IServiceCollection AddVettaAuthenticationFlow<TCompletion>(this IServiceCollection services)
        where TCompletion : class, IVettaLoginCompletion
    {
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-Vetta-CSRF";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IVettaAttemptStore, DevelopmentAttemptStore>();
        services.AddScoped<IVettaLoginCompletion, TCompletion>();
        services.AddScoped<VettaLoginFlow>();
        return services;
    }

    public static IServiceCollection AddVettaCustomerPasskeys(this IServiceCollection services)
    {
        services.AddScoped<VettaPasskeyFlow>();
        return services;
    }

    public static RouteGroupBuilder MapVettaAuthentication(this IEndpointRouteBuilder endpoints, string prefix = "/auth/vetta")
    {
        if (endpoints.ServiceProvider.GetRequiredService<IVettaAttemptStore>() is DevelopmentAttemptStore &&
            !endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment())
            throw new InvalidOperationException("Register a production IVettaAttemptStore before mapping Vetta endpoints outside Development.");
        var group = endpoints.MapGroup(prefix);
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (!http.Request.IsHttps) return Results.BadRequest(new { error = "https_required" });
            if (HttpMethods.IsPost(http.Request.Method))
            {
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
                catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "invalid_csrf" }); }
            }
            return await next(context);
        });
        group.MapGet("/session", (HttpContext http, IAntiforgery antiforgery) =>
        {
            if (Browser(http) is null)
                http.Response.Cookies.Append(BrowserCookie, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true });
            return Results.Ok(new { token = antiforgery.GetAndStoreTokens(http).RequestToken });
        });
        group.MapPost("/start", async (StartRequest request, HttpContext http, VettaLoginFlow flow) =>
        {
            var browser = Browser(http);
            if (browser is null) return Results.BadRequest(new { error = "session_required" });
            try { return Results.Ok(await flow.StartAsync(browser, request, http.RequestAborted)); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "invalid_input" }); }
            catch (TimeoutException) { return Results.Ok(new LoginReply("Unavailable")); }
        });
        group.MapPost("/status", async (HandleRequest request, HttpContext http, VettaLoginFlow flow) =>
        {
            var browser = Browser(http);
            if (browser is null || !Valid(request.Handle)) return Results.BadRequest(new { error = "invalid_attempt" });
            return Results.Ok(await flow.StatusAsync(http, browser, request.Handle, http.RequestAborted));
        });
        group.MapPost("/cancel", (HandleRequest request, HttpContext http, VettaLoginFlow flow) =>
        {
            var browser = Browser(http);
            if (browser is null || !Valid(request.Handle)) return Results.BadRequest(new { error = "invalid_attempt" });
            return Results.Ok(flow.Cancel(browser, request.Handle));
        });
        if (endpoints.ServiceProvider.GetRequiredService<IServiceProviderIsService>().IsService(typeof(VettaPasskeyFlow)))
        {
        group.MapPost("/passkeys/manage", async (PasskeyManageRequest request, HttpContext http, VettaPasskeyFlow flow) =>
        {
            var browser = Browser(http);
            if (browser is null || !Valid(request.CompletedLoginHandle) || request.CredentialId?.Length > 2048) return Results.BadRequest();
            var result = await flow.ManageAsync(browser, request.CompletedLoginHandle, request.CredentialId, http.RequestAborted);
            return Results.Ok(new { result.State, result.Passkeys });
        });
        group.MapPost("/passkeys/options", async (PasskeyOptionsRequest request, HttpContext http, VettaPasskeyFlow flow) =>
        {
            var browser = Browser(http);
            if (browser is null || (request.CompletedLoginHandle is not null && !Valid(request.CompletedLoginHandle)))
                return Results.BadRequest();
            return Results.Ok(await flow.OptionsAsync(browser, request.CompletedLoginHandle, http.RequestAborted));
        });
        group.MapPost("/passkeys/finish", async (PasskeyFinishRequest request, HttpContext http, VettaPasskeyFlow flow) =>
        {
            var browser = Browser(http);
            if (browser is null || !Valid(request.Handle) || request.CredentialJson is not { Length: > 0 and <= 60000 })
                return Results.BadRequest();
            return Results.Ok(await flow.FinishAsync(http, browser, request.Handle, request.CredentialJson, http.RequestAborted));
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(65536));
        group.MapPost("/passkeys/cancel", async (HandleRequest request, HttpContext http, VettaPasskeyFlow flow) =>
        {
            var browser = Browser(http);
            if (browser is null || !Valid(request.Handle)) return Results.BadRequest();
            return Results.Ok(await flow.CancelAsync(browser, request.Handle, http.RequestAborted));
        });
        }
        group.MapGet("/client.js", () => Results.Stream(typeof(Endpoints).Assembly.GetManifestResourceStream(
            "Vetta.Connector.AspNetCore.vetta-login.js")!, "text/javascript"));
        return group;
    }
    private static string? Browser(HttpContext http) => http.Request.Cookies.TryGetValue(BrowserCookie, out var value) && Valid(value) ? value : null;
    private static bool Valid(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    public sealed record PasskeyManageRequest(string CompletedLoginHandle, string? CredentialId = null);
    public sealed record PasskeyOptionsRequest(string? CompletedLoginHandle = null);
    public sealed record PasskeyFinishRequest(string Handle, string CredentialJson);
    public sealed record HandleRequest(string Handle);
}
