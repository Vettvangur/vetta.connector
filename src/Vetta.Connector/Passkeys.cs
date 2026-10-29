using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Vetta.Connector;

/// <summary>Vetta uses a shared passkey; CustomerDomain requires a separately enrolled passkey on the configured origin.</summary>
public sealed record PasskeyPolicy(string Scope, string? Origin, bool PreferPasskeys);
public sealed record PasskeyResponse(string State, Guid? CeremonyId = null, string? OptionsJson = null, VerifiedIdentity? Identity = null, PasskeyDetails[]? Passkeys = null) { public string? ErrorCode { get; init; } public int? HttpStatusCode { get; init; } }
public sealed record PasskeyDetails(string Id, DateTimeOffset CreatedAt);
public interface IVettaPasskeyClient
{
    Task<PasskeyResponse> ListAsync(string origin, string browserBinding, Guid verifiedAuthenticationId, CancellationToken ct = default) => Task.FromResult(new PasskeyResponse("Unavailable"));
    Task<PasskeyResponse> RemoveAsync(string origin, string browserBinding, Guid verifiedAuthenticationId, string credentialId, CancellationToken ct = default) => Task.FromResult(new PasskeyResponse("Unavailable"));
    Task<PasskeyPolicy?> GetPolicyAsync(CancellationToken ct = default);
    Task<PasskeyResponse> RegistrationOptionsAsync(string origin, string browserBinding, Guid verifiedAuthenticationId, CancellationToken ct = default);
    Task<PasskeyResponse> LoginOptionsAsync(string origin, string browserBinding, CancellationToken ct = default);
    Task<PasskeyResponse> FinishRegistrationAsync(string origin, string browserBinding, Guid ceremonyId, string credentialJson, CancellationToken ct = default);
    Task<PasskeyResponse> FinishLoginAsync(string origin, string browserBinding, Guid ceremonyId, string credentialJson, CancellationToken ct = default);
    Task<PasskeyResponse> CancelAsync(string origin, string browserBinding, Guid ceremonyId, CancellationToken ct = default);
}

/// <summary>Backend-only client. Never expose an API key, browser binding or registration proof to JavaScript.</summary>
public sealed class VettaPasskeyClient : IVettaPasskeyClient
{
    private readonly HttpClient http;
    private readonly Uri baseUrl;
    private readonly string application, apiKey;
    private readonly TimeSpan timeout;
    private sealed record Wire(string State, Guid? CeremonyId = null, string? OptionsJson = null, VerifiedIdentity? Identity = null,
        string? Scope = null, string? Origin = null, bool PreferPasskeys = false, PasskeyDetails[]? Passkeys = null) { public string? ErrorCode { get; init; } public int? HttpStatusCode { get; init; } }
    public VettaPasskeyClient(HttpClient http, IOptions<VettaConnectorOptions> options)
    {
        options.Value.Validate();
        this.http = http; baseUrl = options.Value.BaseUrl!; application = options.Value.ApplicationId;
        apiKey = options.Value.ApiKey; timeout = options.Value.RequestTimeout;
    }
    public async Task<PasskeyPolicy?> GetPolicyAsync(CancellationToken ct = default)
    {
        var result = await Send("policy", "", null, null, null, null, ct);
        if (result.State != "Policy" || result.Scope is not ("Vetta" or "CustomerDomain")) return null;
        if (result.Scope == "CustomerDomain" && !ValidOrigin(result.Origin)) return null;
        return new(result.Scope, result.Origin, result.PreferPasskeys);
    }
    public Task<PasskeyResponse> ListAsync(string origin, string browserBinding, Guid verifiedAuthenticationId, CancellationToken ct = default) =>
        Call("list", origin, browserBinding, verifiedAuthenticationId, null, null, ct);
    public Task<PasskeyResponse> RemoveAsync(string origin, string browserBinding, Guid verifiedAuthenticationId, string credentialId, CancellationToken ct = default) =>
        Call("remove", origin, browserBinding, verifiedAuthenticationId, null, credentialId, ct);
    public Task<PasskeyResponse> RegistrationOptionsAsync(string origin, string browserBinding, Guid verifiedAuthenticationId, CancellationToken ct = default)
    {
        if (verifiedAuthenticationId == Guid.Empty) throw new ArgumentException("A recent verified authentication identifier is required.");
        return Call("register-options", origin, browserBinding, verifiedAuthenticationId, null, null, ct);
    }
    public Task<PasskeyResponse> LoginOptionsAsync(string origin, string browserBinding, CancellationToken ct = default) =>
        Call("login-options", origin, browserBinding, null, null, null, ct);
    public Task<PasskeyResponse> FinishRegistrationAsync(string origin, string browserBinding, Guid ceremonyId, string credentialJson, CancellationToken ct = default) =>
        Call("register-finish", origin, browserBinding, null, ceremonyId, credentialJson, ct);
    public Task<PasskeyResponse> FinishLoginAsync(string origin, string browserBinding, Guid ceremonyId, string credentialJson, CancellationToken ct = default) =>
        Call("login-finish", origin, browserBinding, null, ceremonyId, credentialJson, ct);
    public Task<PasskeyResponse> CancelAsync(string origin, string browserBinding, Guid ceremonyId, CancellationToken ct = default) =>
        Call("cancel", origin, browserBinding, null, ceremonyId, null, ct);

    private async Task<PasskeyResponse> Call(string operation, string origin, string binding, Guid? proof, Guid? ceremony, string? credential, CancellationToken ct)
    {
        if (!ValidOrigin(origin) || binding is not { Length: 64 } || !binding.All(Uri.IsHexDigit))
            throw new ArgumentException("An exact HTTPS origin and a 32-byte hex browser binding are required.");
        if ((operation.EndsWith("-finish") || operation == "cancel") && (ceremony is null || ceremony == Guid.Empty))
            throw new ArgumentException("A ceremony identifier is required.");
        if (operation.EndsWith("-finish") && credential is not { Length: > 0 and <= 60000 })
            throw new ArgumentException("A credential response is required.");
        var result = await Send(operation, binding, origin, proof, ceremony, credential, ct);
        if (result.State == "Options" && (!operation.EndsWith("-options") || result.CeremonyId is null || result.CeremonyId == Guid.Empty ||
            result.OptionsJson is not { Length: > 0 and <= 16000 })) return new("Unavailable");
        if (result.State == "Succeeded" && (!operation.EndsWith("-finish") ||
            result.Identity is not { Name.Length: > 0, Ssn.Length: 10 })) return new("Unavailable");
        if (result.State == "PasskeyList" && (operation is not ("list" or "remove") || result.Passkeys is null || result.Passkeys.Length > 10)) return new("Unavailable");
        if (result.State is not ("PasskeyList" or "Options" or "Succeeded" or "Failed" or "Unavailable" or "Cancelled")) return new("Unavailable");
        return new(result.State, result.CeremonyId, result.OptionsJson, result.State == "Succeeded" ? result.Identity : null, result.Passkeys) { ErrorCode = result.ErrorCode, HttpStatusCode = result.HttpStatusCode };
    }
    private static bool ValidOrigin(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 && value == uri.GetLeftPart(UriPartial.Authority);
    private async Task<Wire> Send(string operation, string binding, string? origin, Guid? proof, Guid? ceremony, string? credential, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "passkeys/" + operation))
            {
                Content = JsonContent.Create(new { requestingApplication = application, browserBinding = binding,
                    origin, proofId = proof, ceremonyId = ceremony, credentialJson = credential })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("apiKey", apiKey);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            JsonElement body = default;
            try { body = await VettaProtocol.ReadAsync(response.Content, deadline.Token).ConfigureAwait(false); }
            catch (JsonException) when (!response.IsSuccessStatusCode) { }
            if (!response.IsSuccessStatusCode) return new("Unavailable") { ErrorCode = VettaProtocol.SafeCode(body), HttpStatusCode = (int)response.StatusCode };
            var result = body.Deserialize<Wire>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new("Unavailable");
            return result with { ErrorCode = VettaProtocol.SafeCode(body) };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new("Unavailable"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException) { return new("Unavailable"); }
    }
}
