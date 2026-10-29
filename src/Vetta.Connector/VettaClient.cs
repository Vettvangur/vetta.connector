using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Vetta.Connector;

public sealed class VettaClient : IVettaClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private const int MaxResponseBytes = 65536;
    private readonly HttpClient http;
    private readonly VettaConnectorOptions options;

    public VettaClient(HttpClient http, IOptions<VettaConnectorOptions> options)
    {
        options.Value.Validate();
        this.http = http;
        // Snapshot configuration so an in-flight request cannot switch application or credentials.
        var value = options.Value;
        this.options = new() { BaseUrl = value.BaseUrl, ApplicationId = value.ApplicationId,
            ApiKey = value.ApiKey, Language = value.Language, RequestTimeout = value.RequestTimeout };
    }

    public Task<AuthenticationResult> StartAsync(StartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync("authorize-code", new
        {
            requestingApplication = options.ApplicationId, lang = request.Language ?? options.Language,
            authType = Method(request.Method), phoneNumberOrSsn = Normalize(request.PhoneNumberOrSsn, request.Method),
            challenge = request.Challenge, useEmojiChallenge = request.UseEmojiChallenge,
            simChallengePrefix = request.SimChallengePrefix, appAdditionalMessage = request.AppAdditionalMessage
        }, true, false, null, cancellationToken);
    }

    public Task<AuthenticationResult> AuthenticateSimpleAsync(AuthenticationMethod method, string phoneNumberOrSsn,
        string? message = null, CancellationToken cancellationToken = default) =>
        SendAsync("authorize-simple", new { requestingApplication = options.ApplicationId, lang = options.Language,
            authType = Method(method), phoneNumberOrSsn = Normalize(phoneNumberOrSsn, method), challengeOrMessage = message },
            false, true, null, cancellationToken);

    public Task<AuthenticationResult> GetStatusAsync(Guid identifier, CancellationToken cancellationToken = default) =>
        StatusAsync(identifier, "authorize-status", cancellationToken);
    public Task<AuthenticationResult> WaitForStatusAsync(Guid identifier, CancellationToken cancellationToken = default) =>
        StatusAsync(identifier, "authorize-status-poll", cancellationToken);

    private Task<AuthenticationResult> StatusAsync(Guid identifier, string route, CancellationToken cancellationToken)
    {
        if (identifier == Guid.Empty) throw new ArgumentException("An authentication identifier is required.", nameof(identifier));
        return SendAsync(route, new { requestingApplication = options.ApplicationId, lang = options.Language, identifier },
            false, false, identifier, cancellationToken);
    }

    private async Task<AuthenticationResult> SendAsync(string route, object payload, bool start, bool simple,
        Guid? identifier, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUrl!, route))
            { Content = JsonContent.Create(payload, options: JsonOptions) };
            request.Headers.Authorization = new AuthenticationHeaderValue("apiKey", options.ApiKey);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                JsonElement errorBody = default;
                try { errorBody = await VettaProtocol.ReadAsync(response.Content, timeout.Token).ConfigureAwait(false); }
                catch (JsonException) { /* Legacy endpoints may return empty or non-JSON errors. */ }
                return VettaProtocol.Failure(response.StatusCode, identifier, errorBody);
            }
            if (response.Content.Headers.ContentLength > MaxResponseBytes) return Invalid(identifier);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes) return Invalid(identifier);
                buffer.Write(bytes, 0, read);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalid(identifier);
            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                if (error.ValueKind != JsonValueKind.String) return Invalid(identifier);
                if (!string.IsNullOrWhiteSpace(error.GetString()))
                    return new(AuthenticationState.Failed, identifier, Error: AuthenticationError.AuthenticationFailed) { ErrorCode = VettaProtocol.SafeCode(root) };
            }
            if (start)
            {
                if (!root.TryGetProperty("identifier", out var id) || id.ValueKind != JsonValueKind.String ||
                    !id.TryGetGuid(out var parsedId) || parsedId == Guid.Empty ||
                    !root.TryGetProperty("challenge", out var challenge) || challenge.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(challenge.GetString())) return Invalid(identifier);
                return new(AuthenticationState.Started, parsedId, challenge.GetString());
            }
            if (!simple)
            {
                if (!root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("finishedPendingUser", out var finished) ||
                    finished.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Invalid(identifier);
                switch (status.GetString())
                {
                    case "Pending" when !finished.GetBoolean(): return new(AuthenticationState.Pending, identifier);
                    case "Failed" when finished.GetBoolean():
                        return new(AuthenticationState.Failed, identifier, Error: AuthenticationError.AuthenticationFailed) { ErrorCode = VettaProtocol.SafeCode(root) };
                    case "Success" when finished.GetBoolean(): break;
                    default: return Invalid(identifier);
                }
            }
            if (!root.TryGetProperty("userInfo", out var user) || user.ValueKind != JsonValueKind.Object ||
                !user.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !user.TryGetProperty("ssn", out var ssn) || ssn.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(name.GetString()) || string.IsNullOrWhiteSpace(ssn.GetString())) return Invalid(identifier);
            return new(AuthenticationState.Succeeded, identifier, Identity: new(name.GetString()!, ssn.GetString()!));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new TimeoutException("The Vetta request timed out; its outcome may be unknown."); }
        catch (JsonException) { return Invalid(identifier); }
        catch (HttpRequestException) { return new(AuthenticationState.Unavailable, identifier, Error: AuthenticationError.TransportFailure); }
        catch (IOException) { return new(AuthenticationState.Unavailable, identifier, Error: AuthenticationError.TransportFailure); }
    }

    private static AuthenticationResult Invalid(Guid? id) => new(AuthenticationState.Unavailable, id, Error: AuthenticationError.InvalidResponse);
    private static string Method(AuthenticationMethod method) => method switch
    {
        AuthenticationMethod.App => "app", AuthenticationMethod.Sim => "sim",
        _ => throw new ArgumentException("Only app and SIM authentication are supported.", nameof(method))
    };
    private static string Normalize(string input, AuthenticationMethod method)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = new string(input.Where(c => c is >= '0' and <= '9').ToArray());
        if (method == AuthenticationMethod.Sim)
        {
            if (normalized.StartsWith("00354", StringComparison.Ordinal)) normalized = normalized[5..];
            else if (normalized.Length == 10 && normalized.StartsWith("354", StringComparison.Ordinal)) normalized = normalized[3..];
        }
        if (normalized.Length != (method == AuthenticationMethod.Sim ? 7 : 10))
            throw new ArgumentException("Invalid phone number or kennitala format.", nameof(input));
        return normalized;
    }
}
