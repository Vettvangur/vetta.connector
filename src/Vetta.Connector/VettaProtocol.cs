using System.Net;
using System.Text.Json;
namespace Vetta.Connector;

internal static class VettaProtocol
{
    internal static async Task<JsonElement> ReadAsync(HttpContent content, CancellationToken ct)
    {
        const int max = 65536;
        if (content.Headers.ContentLength > max) throw new JsonException("Response exceeds limit.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var bytes = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(bytes, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > max) throw new JsonException("Response exceeds limit.");
            buffer.Write(bytes, 0, count);
        }
        using var json = JsonDocument.Parse(buffer.ToArray());
        return json.RootElement.Clone();
    }
    internal static string? Text(JsonElement body, string key) => body.ValueKind == JsonValueKind.Object &&
        body.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static string? SafeCode(JsonElement body) => Text(body, "errorCode") is
        "user_not_found" or "rejected" or "timeout" or "credential_blocked" or "credential_unavailable" or "wrong_challenge" or
        "request_in_progress" or "provider_unavailable" or "provider_timeout" or "interrupted" or "invalid_callback" or
        "unknown_login" or "ended" or "passkey_failed" or "passkey_not_registered" or "passkey_limit" ? Text(body, "errorCode") : null;
    internal static AuthenticationResult Failure(HttpStatusCode status, Guid? id, JsonElement body)
    {
        var code = SafeCode(body);
        var terminal = code is "user_not_found" or "rejected" or "timeout" or "credential_blocked" or "credential_unavailable" or "wrong_challenge" or "ended";
        return new(terminal ? AuthenticationState.Failed : AuthenticationState.Unavailable, id, Error: terminal ? AuthenticationError.AuthenticationFailed : status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AuthenticationError.Unauthorized,
            HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Gone => AuthenticationError.InvalidRequest,
            HttpStatusCode.TooManyRequests => AuthenticationError.RateLimited,
            _ => AuthenticationError.ServerUnavailable
        }) { ErrorCode = code, HttpStatusCode = (int)status };
    }
    internal static AuthenticationResult Status(JsonElement body, Guid id)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("finishedPendingUser", out var done) || done.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Invalid(id);
        switch (Text(body, "status"))
        {
            case "Pending" when !done.GetBoolean(): return new(AuthenticationState.Pending, id);
            case "Failed" when done.GetBoolean(): return new(AuthenticationState.Failed, id, Error: AuthenticationError.AuthenticationFailed) { ErrorCode = SafeCode(body) };
            case "Success" when done.GetBoolean():
                if (!body.TryGetProperty("userInfo", out var user)) return Invalid(id);
                var name = Text(user, "name"); var ssn = Text(user, "ssn");
                return !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(ssn) ? new(AuthenticationState.Succeeded, id, Identity: new(name, ssn)) : Invalid(id);
            default: return Invalid(id);
        }
    }
    internal static AuthenticationResult Invalid(Guid? id) => new(AuthenticationState.Unavailable, id, Error: AuthenticationError.InvalidResponse);
}
