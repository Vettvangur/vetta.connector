using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Vetta.Connector;

/// <summary>Credentials of a Vetta machine client (client-credentials grant). Keep the secret server-side only.</summary>
public sealed class VettaMachineTokenOptions
{
    /// <summary>The Vetta issuer, e.g. <c>https://api.vetta.is/</c>. Tokens are requested from <c>{Authority}connect/token</c>.</summary>
    public Uri? Authority { get; set; }
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    /// <summary>Scopes to request. Empty requests every scope the client is allowed.</summary>
    public ICollection<string> Scopes { get; } = new List<string>();
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal VettaMachineTokenOptions Validated()
    {
        if (Authority is not { IsAbsoluteUri: true, Scheme: "https" } authority || authority.UserInfo.Length != 0 || authority.Query.Length != 0 || authority.Fragment.Length != 0)
            throw new ArgumentException("Vetta authority must be an absolute HTTPS URL.");
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
            throw new ArgumentException("A machine client ID and secret are required.");
        if (Scopes.Any(s => string.IsNullOrWhiteSpace(s) || s.Any(char.IsWhiteSpace)))
            throw new ArgumentException("Scopes must be non-empty and contain no whitespace.");
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentException("RequestTimeout must be between zero and five minutes.");
        // Copy so later changes to the caller's instance cannot switch credentials mid-flight.
        var copy = new VettaMachineTokenOptions
        {
            Authority = new Uri(authority.AbsoluteUri.EndsWith('/') ? authority.AbsoluteUri : authority.AbsoluteUri + "/"),
            ClientId = ClientId, ClientSecret = ClientSecret, RequestTimeout = RequestTimeout,
        };
        foreach (var scope in Scopes.Distinct(StringComparer.Ordinal)) copy.Scopes.Add(scope);
        return copy;
    }
}

/// <summary>Supplies a cached client-credentials access token, refreshing it shortly before it expires.</summary>
public interface IVettaMachineTokenProvider
{
    ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Vetta refused or could not answer a token request. The message never contains the client secret.</summary>
public sealed class VettaMachineTokenException(string message, string? error = null, HttpStatusCode? statusCode = null) : Exception(message)
{
    /// <summary>The OAuth error code, such as <c>invalid_client</c> or <c>invalid_scope</c>, when Vetta returned one.</summary>
    public string? Error { get; } = error;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class VettaMachineTokenProvider : IVettaMachineTokenProvider, IDisposable
{
    internal const int MaxResponseBytes = 65536;
    /// <summary>Refresh this long before expiry so a token never expires in flight.</summary>
    internal static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);
    private readonly Func<HttpClient> client;
    private readonly VettaMachineTokenOptions options;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim refresh = new(1, 1);
    private (string Token, DateTimeOffset RefreshAt)? cached;

    public VettaMachineTokenProvider(Func<HttpClient> client, VettaMachineTokenOptions options, TimeProvider? clock = null)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.options = (options ?? throw new ArgumentNullException(nameof(options))).Validated();
        this.clock = clock ?? TimeProvider.System;
    }

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (cached is { } current && clock.GetUtcNow() < current.RefreshAt) return current.Token;
        await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (cached is { } fresh && clock.GetUtcNow() < fresh.RefreshAt) return fresh.Token;
            var (token, lifetime) = await RequestAsync(cancellationToken).ConfigureAwait(false);
            var margin = lifetime > RefreshMargin * 2 ? RefreshMargin : lifetime / 2;
            cached = (token, clock.GetUtcNow() + lifetime - margin);
            return token;
        }
        finally { refresh.Release(); }
    }

    private async Task<(string Token, TimeSpan Lifetime)> RequestAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
        if (options.Scopes.Count > 0) form["scope"] = string.Join(' ', options.Scopes);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.Authority!, "connect/token")) { Content = new FormUrlEncodedContent(form) };
        // RFC 6749 §2.3.1: form-urlencode the ID and secret before Basic encoding.
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
            Uri.EscapeDataString(options.ClientId) + ":" + Uri.EscapeDataString(options.ClientSecret))));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var body = await ReadAsync(response, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = body is { } e && e.TryGetProperty("error", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
                var safe = error is { Length: <= 64 } && error.All(c => c is >= 'a' and <= 'z' or '_') ? error : null;
                throw new VettaMachineTokenException($"Vetta refused the token request ({safe ?? ((int)response.StatusCode).ToString()}).", safe, response.StatusCode);
            }
            if (body is not { } json || !json.TryGetProperty("access_token", out var accessToken) || accessToken.GetString() is not { Length: > 0 } token ||
                !json.TryGetProperty("token_type", out var type) || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) ||
                !json.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt32(out var seconds) || seconds <= 0)
                throw new VettaMachineTokenException("Vetta returned an invalid token response.");
            return (token, TimeSpan.FromSeconds(seconds));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Vetta token request timed out.");
        }
    }

    private static async Task<JsonElement?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new VettaMachineTokenException("Vetta returned an oversized token response.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new VettaMachineTokenException("Vetta returned an oversized token response.");
            buffer.Write(bytes, 0, read);
        }
        if (buffer.Length == 0) return null;
        try { using var document = JsonDocument.Parse(buffer.ToArray()); return document.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    public void Dispose() => refresh.Dispose();
}

/// <summary>Adds <c>Authorization: Bearer</c> with a cached Vetta machine token to outgoing requests.</summary>
public sealed class VettaMachineTokenHandler(IVettaMachineTokenProvider provider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await provider.GetTokenAsync(cancellationToken).ConfigureAwait(false));
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public static class VettaMachineTokenRegistration
{
    private const string TokenClientPrefix = "Vetta.MachineToken:";

    /// <summary>
    /// Call another API with Vetta machine tokens: every request from this <see cref="HttpClient"/> carries a cached
    /// client-credentials token. Each configured client has its own token cache. Options are validated immediately.
    /// </summary>
    public static IHttpClientBuilder AddVettaMachineToken(this IHttpClientBuilder builder, Action<VettaMachineTokenOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var key = Register(builder.Services, TokenClientPrefix + builder.Name, configure);
        builder.AddHttpMessageHandler(sp => new VettaMachineTokenHandler(sp.GetRequiredKeyedService<IVettaMachineTokenProvider>(key)));
        return builder;
    }

    /// <summary>Register an <see cref="IVettaMachineTokenProvider"/> for direct use (e.g. gRPC or custom clients).</summary>
    public static IServiceCollection AddVettaMachineTokenProvider(this IServiceCollection services, Action<VettaMachineTokenOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        var key = Register(services, TokenClientPrefix + "default", configure);
        services.AddSingleton(sp => sp.GetRequiredKeyedService<IVettaMachineTokenProvider>(key));
        return services;
    }

    private static string Register(IServiceCollection services, string key, Action<VettaMachineTokenOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new VettaMachineTokenOptions();
        configure(options);
        var validated = options.Validated();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(key)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        services.AddKeyedSingleton<IVettaMachineTokenProvider>(key, (sp, _) =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new VettaMachineTokenProvider(() => factory.CreateClient(key), validated, sp.GetRequiredService<TimeProvider>());
        });
        return key;
    }
}
