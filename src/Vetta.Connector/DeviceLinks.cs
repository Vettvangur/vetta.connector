using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
namespace Vetta.Connector;

public enum DeviceLinkMethod { Qr, Web2App }
public sealed record DeviceLinkStartRequest(DeviceLinkMethod Method, Uri? CallbackUrl = null, string? Message = null, string? RestrictToSsn = null);
public sealed record DeviceLinkStartResult(AuthenticationResult Authentication, DeviceLinkMethod Method, string? QrLink = null, string? AppLink = null, string? AndroidAppLink = null);
public sealed record DeviceLinkQrResult(string? Link, AuthenticationError? Error = null, string? ErrorCode = null, int? HttpStatusCode = null);
public interface IVettaDeviceLinkClient
{
    Task<DeviceLinkStartResult> StartAsync(DeviceLinkStartRequest request, CancellationToken ct = default);
    Task<DeviceLinkQrResult> GetQrAsync(Guid identifier, CancellationToken ct = default);
    Task<AuthenticationResult> GetStatusAsync(Guid identifier, CancellationToken ct = default);
    Task<AuthenticationResult> WaitForStatusAsync(Guid identifier, CancellationToken ct = default);
    Task<AuthenticationResult> CompleteCallbackAsync(Guid identifier, Uri callbackUrl, CancellationToken ct = default);
}

/// <summary>Backend-only device link client. Correlate callbacks with the initiating browser, not a URL-supplied identifier.</summary>
public sealed class VettaDeviceLinkClient : IVettaDeviceLinkClient
{
    private readonly HttpClient http;
    private readonly Uri baseUrl;
    private readonly string application, key, language;
    private readonly TimeSpan timeout;
    public VettaDeviceLinkClient(HttpClient http, IOptions<VettaConnectorOptions> configuration)
    {
        var options = configuration.Value; options.Validate(); this.http = http;
        baseUrl = options.BaseUrl!; application = options.ApplicationId; key = options.ApiKey; language = options.Language; timeout = options.RequestTimeout;
    }
    public async Task<DeviceLinkStartResult> StartAsync(DeviceLinkStartRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Method is not (DeviceLinkMethod.Qr or DeviceLinkMethod.Web2App)) throw new ArgumentException("Unsupported device-link method.");
        if (request.Message?.Length > 60) throw new ArgumentException("Message must be at most 60 characters.");
        if (request.RestrictToSsn is not null && (request.RestrictToSsn.Length != 10 || !request.RestrictToSsn.All(char.IsAsciiDigit))) throw new ArgumentException("Expected a ten-digit kennitala.");
        if (request.Method == DeviceLinkMethod.Web2App && !Callback(request.CallbackUrl)) throw new ArgumentException("An absolute HTTPS callback URL is required.");
        var flow = request.Method == DeviceLinkMethod.Qr ? "QR" : "Web2App";
        var (body, failure) = await SendAsync("devicelink/start", new { requestingApplication = application, flow, callbackUrl = request.CallbackUrl?.AbsoluteUri, message = request.Message, restrictToSsn = request.RestrictToSsn, lang = language }, null, ct);
        if (failure is not null) return new(failure, request.Method);
        if (VettaProtocol.Text(body, "flow") != flow || !Guid.TryParse(VettaProtocol.Text(body, "identifier"), out var id) || id == Guid.Empty) return new(VettaProtocol.Invalid(null), request.Method);
        var qr = request.Method == DeviceLinkMethod.Qr ? VettaProtocol.Text(body, "qrLink") : null;
        var app = request.Method == DeviceLinkMethod.Web2App ? VettaProtocol.Text(body, "appLink") : null;
        if (!SafeAppLink(request.Method == DeviceLinkMethod.Qr ? qr : app)) return new(VettaProtocol.Invalid(id), request.Method);
        // Build the intent locally from the validated HTTPS URL, never trust arbitrary URI schemes from a response.
        var android = app is null ? null : $"intent://{app[8..]}#Intent;scheme=https;package=is.audkenni.app;S.browser_fallback_url={Uri.EscapeDataString(app)};end";
        return new(new(AuthenticationState.Started, id), request.Method, qr, app, android);
    }
    public async Task<DeviceLinkQrResult> GetQrAsync(Guid identifier, CancellationToken ct = default)
    {
        ValidId(identifier);
        var (body, failure) = await SendAsync($"devicelink/qr?identifier={identifier:D}&requestingApplication={Uri.EscapeDataString(application)}", null, identifier, ct);
        if (failure is not null) return new(null, failure.Error, failure.ErrorCode, failure.HttpStatusCode);
        var link = VettaProtocol.Text(body, "link");
        return SafeAppLink(link) ? new(link) : new(null, AuthenticationError.InvalidResponse);
    }
    public Task<AuthenticationResult> GetStatusAsync(Guid identifier, CancellationToken ct = default) => Status(identifier, "devicelink/status", null, ct);
    public Task<AuthenticationResult> WaitForStatusAsync(Guid identifier, CancellationToken ct = default) => Status(identifier, "devicelink/status-poll", null, ct);
    public Task<AuthenticationResult> CompleteCallbackAsync(Guid identifier, Uri callbackUrl, CancellationToken ct = default)
    {
        if (!Callback(callbackUrl) || callbackUrl.AbsoluteUri.Length > 16000) throw new ArgumentException("Invalid callback URL.");
        return Status(identifier, "devicelink/callback", callbackUrl.AbsoluteUri, ct);
    }
    private async Task<AuthenticationResult> Status(Guid id, string route, string? callbackUrl, CancellationToken ct)
    {
        ValidId(id);
        var (body, failure) = await SendAsync(route, new { requestingApplication = application, identifier = id, callbackUrl, lang = language }, id, ct);
        return failure ?? VettaProtocol.Status(body, id);
    }
    private async Task<(JsonElement, AuthenticationResult?)> SendAsync(string route, object? data, Guid? id, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(data is null ? HttpMethod.Get : HttpMethod.Post, new Uri(baseUrl, route));
            request.Headers.Authorization = new AuthenticationHeaderValue("apikey", key);
            if (data is not null) request.Content = JsonContent.Create(data);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            JsonElement body = default;
            try { body = await VettaProtocol.ReadAsync(response.Content, limit.Token).ConfigureAwait(false); }
            catch (JsonException) when (!response.IsSuccessStatusCode) { }
            return !response.IsSuccessStatusCode ? (body, VettaProtocol.Failure(response.StatusCode, id, body)) :
                VettaProtocol.Text(body, "error") is { Length: > 0 } ? (body, new(AuthenticationState.Failed, id, Error: AuthenticationError.AuthenticationFailed) { ErrorCode = VettaProtocol.SafeCode(body) }) : (body, (AuthenticationResult?)null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new TimeoutException("The Vetta request timed out; its outcome may be unknown."); }
        catch (JsonException) { return (default, VettaProtocol.Invalid(id)); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return (default, new(AuthenticationState.Unavailable, id, Error: AuthenticationError.TransportFailure)); }
    }
    private static void ValidId(Guid id) { if (id == Guid.Empty) throw new ArgumentException("An authentication identifier is required."); }
    private static bool Callback(Uri? uri) => uri is { IsAbsoluteUri: true, Scheme: "https", UserInfo.Length: 0, Fragment.Length: 0 };
    internal static bool SafeAppLink(string? value) => value is { Length: > 0 and <= 12000 } && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "www.audkenniapp.is" && uri.IsDefaultPort && uri.AbsolutePath == "/device-link" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
}
