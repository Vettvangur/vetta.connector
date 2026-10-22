namespace Vetta.Connector;

public enum AuthenticationMethod { Sim = 0, App = 2 }
public enum AuthenticationState { Started, Pending, Succeeded, Failed, Unavailable }
public enum AuthenticationError { InvalidResponse, InvalidRequest, Unauthorized, RateLimited, ServerUnavailable, AuthenticationFailed, TransportFailure }
public sealed record VerifiedIdentity(string Name, string Ssn);
public sealed record AuthenticationResult(AuthenticationState State, Guid? Identifier = null,
    string? Challenge = null, VerifiedIdentity? Identity = null, AuthenticationError? Error = null);
public sealed record StartRequest(AuthenticationMethod Method, string PhoneNumberOrSsn,
    string? Challenge = null, bool UseEmojiChallenge = false, string? SimChallengePrefix = null,
    string? AppAdditionalMessage = null, string? Language = null);

public interface IVettaClient
{
    Task<AuthenticationResult> StartAsync(StartRequest request, CancellationToken cancellationToken = default);
    Task<AuthenticationResult> GetStatusAsync(Guid identifier, CancellationToken cancellationToken = default);
    Task<AuthenticationResult> WaitForStatusAsync(Guid identifier, CancellationToken cancellationToken = default);
    Task<AuthenticationResult> AuthenticateSimpleAsync(AuthenticationMethod method, string phoneNumberOrSsn,
        string? message = null, CancellationToken cancellationToken = default);
}

public sealed class VettaConnectorOptions
{
    public Uri? BaseUrl { get; set; }
    public string ApplicationId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Language { get; set; } = "is";
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(100);

    internal void Validate()
    {
        if (BaseUrl is null || !BaseUrl.IsAbsoluteUri || BaseUrl.Scheme != "https" ||
            BaseUrl.UserInfo.Length != 0 || BaseUrl.Query.Length != 0 || BaseUrl.Fragment.Length != 0 ||
            !BaseUrl.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("Vetta BaseUrl must be an absolute HTTPS URL ending in '/'.");
        if (string.IsNullOrWhiteSpace(ApplicationId) || string.IsNullOrWhiteSpace(Language))
            throw new ArgumentException("Vetta ApplicationId and Language are required.");
        if (string.IsNullOrEmpty(ApiKey) || ApiKey.Length > 4096 || ApiKey.Any(c => c < '!' || c > '~' || c == ','))
            throw new ArgumentException("Vetta ApiKey must be a valid nonempty header credential.");
        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > TimeSpan.FromMinutes(5))
            throw new ArgumentException("Vetta RequestTimeout must be between zero and five minutes.");
    }
}
