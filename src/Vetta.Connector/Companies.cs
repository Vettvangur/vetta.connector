using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
namespace Vetta.Connector;

/// <summary>Roles in Vetta's company answers (<c>role</c>, <c>yourRole</c>).</summary>
public static class VettaCompanyRoles
{
    public const string ProcurationHolder = "procuration_holder", Manager = "manager", Member = "member";
}

/// <summary>A company the person can sign in as. <see cref="CanManage"/> is true for prókúruhafar and managers.</summary>
public sealed record VettaCompany(string NationalId, string Name, string Role, bool CanManage, DateTime? ExpiresUtc);
/// <summary>Access someone offered the person; answer it with <see cref="IVettaCompanyClient.AnswerInvitationAsync"/>.</summary>
public sealed record VettaCompanyInvitation(Guid Id, string NationalId, string Name, string Role, string? InvitedBy, DateTime? ExpiresUtc);
/// <param name="RegistryUnavailable">The company register could not be reached: companies the person holds prókúra for are missing. Offer to try again or continue as the person.</param>
public sealed record VettaCompanyChoices(IReadOnlyList<VettaCompany> Companies, IReadOnlyList<VettaCompanyInvitation> Invitations, bool RegistryUnavailable);
/// <param name="Token">Keep it on your server with the person's own session; never send it to the browser.</param>
public sealed record VettaCompanySession(string Token, DateTime ExpiresUtc, string PersonName, VettaCompanyChoices Choices);
/// <summary>The company to sign the person in as, confirmed by Vetta just now.</summary>
public sealed record VettaChosenCompany(string Id, string Name, string Role);
/// <summary>Someone with access to a company. Prókúruhafar have no <see cref="AccessId"/> and cannot be removed; kennitölur are masked.</summary>
public sealed record VettaCompanyPerson(Guid? AccessId, string? Name, string NationalId, string Role, string Status, DateTime? ExpiresUtc,
    DateTime? InvitedUtc, string? InvitedBy, Guid? Revision, bool IsYou, bool Removable);
public sealed record VettaCompanyAccess(string NationalId, string Name, string YourRole, IReadOnlyList<VettaCompanyPerson> People);

/// <summary>
/// The result of a company call. <see cref="Error"/> is one of Vetta's codes (<c>addon_disabled</c>, <c>session_expired</c>,
/// <c>company_forbidden</c>, <c>company_conflict</c>, …), <c>transport_failure</c> or <c>invalid_response</c>.
/// <see cref="Message"/> explains a refusal in English, for logs or a fallback text.
/// </summary>
public sealed record VettaCompanyResult<T>(T? Value, string? Error = null, string? Message = null, int? HttpStatusCode = null)
{
    public bool Succeeded => Error is null && Value is not null;
}

/// <summary>
/// Fyrirtækjaumboð for your own sign-in page (backend only). After a direct sign-in succeeds, open a company session with its
/// identifier within two minutes, then let the person choose a company, answer invitations and, as a prókúruhafi or
/// manager, manage who else may sign in as the company in your organisation's applications.
/// </summary>
public interface IVettaCompanyClient
{
    Task<VettaCompanyResult<VettaCompanySession>> OpenSessionAsync(Guid signInIdentifier, CancellationToken ct = default);
    Task<VettaCompanyResult<VettaCompanyChoices>> GetChoicesAsync(string session, CancellationToken ct = default);
    Task<VettaCompanyResult<VettaChosenCompany>> ChooseAsync(string session, string companyNationalId, CancellationToken ct = default);
    Task<VettaCompanyResult<VettaCompanyChoices>> AnswerInvitationAsync(string session, Guid invitationId, bool accept, CancellationToken ct = default);
    Task<VettaCompanyResult<VettaCompanyAccess>> GetAccessAsync(string session, string companyNationalId, CancellationToken ct = default);
    Task<VettaCompanyResult<VettaCompanyAccess>> InviteAsync(string session, string companyNationalId, string inviteeNationalId, string role,
        DateTime? expiresUtc = null, string? email = null, CancellationToken ct = default);
    Task<VettaCompanyResult<VettaCompanyAccess>> ChangeAccessAsync(string session, string companyNationalId, Guid accessId, Guid revision, string role,
        DateTime? expiresUtc, CancellationToken ct = default);
    Task<VettaCompanyResult<bool>> RemoveAccessAsync(string session, Guid accessId, Guid revision, CancellationToken ct = default);
}

public sealed class VettaCompanyClient : IVettaCompanyClient
{
    // A company's access list can hold hundreds of people.
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly string[] Codes = ["addon_disabled", "session_required", "session_expired", "identifier_required", "sign_in_not_found", "session_exists",
        "company_forbidden", "company_invalid", "company_conflict", "company_not_found", "company_unavailable"];
    private readonly HttpClient http;
    private readonly Uri baseUrl;
    private readonly string application, key;
    private readonly TimeSpan timeout;

    public VettaCompanyClient(HttpClient http, IOptions<VettaConnectorOptions> configuration)
    {
        var options = configuration.Value; options.Validate(); this.http = http;
        baseUrl = options.BaseUrl!; application = options.ApplicationId; key = options.ApiKey; timeout = options.RequestTimeout;
    }

    public async Task<VettaCompanyResult<VettaCompanySession>> OpenSessionAsync(Guid signInIdentifier, CancellationToken ct = default)
    {
        if (signInIdentifier == Guid.Empty) throw new ArgumentException("The identifier of a succeeded sign-in is required.", nameof(signInIdentifier));
        var reply = await SendAsync<SessionReply>("session", new { requestingApplication = application, identifier = signInIdentifier }, ct);
        return reply.Value is { Session: { Length: > 0 } token, Person.Name: { } name, Companies: { } choices }
            ? new(new VettaCompanySession(token, reply.Value.ExpiresUtc, name, choices))
            : Fail<VettaCompanySession, SessionReply>(reply);
    }

    public async Task<VettaCompanyResult<VettaCompanyChoices>> GetChoicesAsync(string session, CancellationToken ct = default) =>
        Choices(await SendAsync<ChoicesReply>("companies", new { requestingApplication = application, session = Session(session) }, ct));

    public async Task<VettaCompanyResult<VettaChosenCompany>> ChooseAsync(string session, string companyNationalId, CancellationToken ct = default)
    {
        var reply = await SendAsync<ChosenReply>("choose", new { requestingApplication = application, session = Session(session), company = Company(companyNationalId) }, ct);
        return reply.Value?.Company is { } company ? new(company) : Fail<VettaChosenCompany, ChosenReply>(reply);
    }

    public async Task<VettaCompanyResult<VettaCompanyChoices>> AnswerInvitationAsync(string session, Guid invitationId, bool accept, CancellationToken ct = default) =>
        Choices(await SendAsync<ChoicesReply>("invitations/answer", new { requestingApplication = application, session = Session(session), accessId = invitationId, accept }, ct));

    public async Task<VettaCompanyResult<VettaCompanyAccess>> GetAccessAsync(string session, string companyNationalId, CancellationToken ct = default) =>
        Access(await SendAsync<AccessReply>("access", new { requestingApplication = application, session = Session(session), company = Company(companyNationalId) }, ct));

    public async Task<VettaCompanyResult<VettaCompanyAccess>> InviteAsync(string session, string companyNationalId, string inviteeNationalId, string role,
        DateTime? expiresUtc = null, string? email = null, CancellationToken ct = default) =>
        Access(await SendAsync<AccessReply>("access/invite", new { requestingApplication = application, session = Session(session), company = Company(companyNationalId),
            invitee = Company(inviteeNationalId), role = Role(role), expiresUtc = Utc(expiresUtc), email }, ct));

    public async Task<VettaCompanyResult<VettaCompanyAccess>> ChangeAccessAsync(string session, string companyNationalId, Guid accessId, Guid revision, string role,
        DateTime? expiresUtc, CancellationToken ct = default) =>
        Access(await SendAsync<AccessReply>("access/change", new { requestingApplication = application, session = Session(session), company = Company(companyNationalId),
            accessId, revision, role = Role(role), expiresUtc = Utc(expiresUtc) }, ct));

    public async Task<VettaCompanyResult<bool>> RemoveAccessAsync(string session, Guid accessId, Guid revision, CancellationToken ct = default)
    {
        var reply = await SendAsync<RemovedReply>("access/remove", new { requestingApplication = application, session = Session(session), accessId, revision }, ct);
        return reply.Value is { Removed: true } ? new(true) : Fail<bool, RemovedReply>(reply);
    }

    private static VettaCompanyResult<VettaCompanyChoices> Choices(VettaCompanyResult<ChoicesReply> reply) =>
        reply.Value?.Companies is { } choices ? new(choices) : Fail<VettaCompanyChoices, ChoicesReply>(reply);
    private static VettaCompanyResult<VettaCompanyAccess> Access(VettaCompanyResult<AccessReply> reply) =>
        reply.Value?.Access is { } access ? new(access) : Fail<VettaCompanyAccess, AccessReply>(reply);
    private static VettaCompanyResult<TOut> Fail<TOut, TIn>(VettaCompanyResult<TIn> reply) => new(default, reply.Error ?? "invalid_response", reply.Message, reply.HttpStatusCode);

    private static string Session(string session) =>
        session is { Length: >= 32 and <= 128 } && session.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? session
            : throw new ArgumentException("A company session token is required.", nameof(session));
    private static string Company(string nationalId)
    {
        var digits = (nationalId ?? "").Replace("-", "").Trim();
        return digits.Length == 10 && digits.All(char.IsAsciiDigit) ? digits : throw new ArgumentException("Expected a ten-digit kennitala.", nameof(nationalId));
    }
    private static string Role(string role) => role is VettaCompanyRoles.Manager or VettaCompanyRoles.Member ? role
        : throw new ArgumentException("Role must be manager or member.", nameof(role));
    private static DateTime? Utc(DateTime? value) => value is { Kind: DateTimeKind.Local } local ? local.ToUniversalTime()
        : value is { Kind: DateTimeKind.Unspecified } plain ? DateTime.SpecifyKind(plain, DateTimeKind.Utc) : value;

    private async Task<VettaCompanyResult<T>> SendAsync<T>(string route, object payload, CancellationToken ct) where T : class
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct); limit.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUrl, "company/v1/" + route)) { Content = JsonContent.Create(payload, options: Json) };
            request.Headers.Authorization = new AuthenticationHeaderValue("apikey", key);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaxResponseBytes) return new(null, "invalid_response", HttpStatusCode: (int)response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(); var bytes = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(bytes, limit.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes) return new(null, "invalid_response", HttpStatusCode: (int)response.StatusCode);
                buffer.Write(bytes, 0, read);
            }
            if (!response.IsSuccessStatusCode)
            {
                ErrorReply? error = null;
                try { if (buffer.Length > 0) error = JsonSerializer.Deserialize<ErrorReply>(buffer.ToArray(), Json); } catch (JsonException) { }
                var code = error?.Error is { } known && Codes.Contains(known) ? known
                    : (int)response.StatusCode == 401 ? "unauthorized" : (int)response.StatusCode == 429 ? "rate_limited" : "server_unavailable";
                return new(null, code, error?.Message is { Length: <= 500 } message ? message : null, (int)response.StatusCode);
            }
            return new(JsonSerializer.Deserialize<T>(buffer.ToArray(), Json));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new TimeoutException("The Vetta request timed out; its outcome may be unknown."); }
        catch (JsonException) { return new(null, "invalid_response"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { return new(null, "transport_failure"); }
    }

    private sealed record ErrorReply(string? Error, string? Message);
    private sealed record Person(string? Name);
    private sealed record SessionReply(string? Session, DateTime ExpiresUtc, Person? Person, VettaCompanyChoices? Companies);
    private sealed record ChoicesReply(VettaCompanyChoices? Companies);
    private sealed record ChosenReply(VettaChosenCompany? Company);
    private sealed record AccessReply(VettaCompanyAccess? Access);
    private sealed record RemovedReply(bool Removed);
}

/// <summary>The company in a Vetta token, when the person signed in as one (hosted login with the <c>company</c> scope).</summary>
public static class VettaCompanyClaims
{
    public const string Scope = "company", Id = "company_id", Name = "company_name", Role = "company_role";

    /// <summary>The company the person chose, or null when they signed in as themselves.</summary>
    public static VettaChosenCompany? From(System.Security.Claims.ClaimsPrincipal? principal)
    {
        string? Value(string type) => principal?.FindFirst(type)?.Value;
        return Value(Id) is { Length: 10 } id && id.All(char.IsAsciiDigit) && Value(Name) is { Length: > 0 } name
            && Value(Role) is VettaCompanyRoles.ProcurationHolder or VettaCompanyRoles.Manager or VettaCompanyRoles.Member
            ? new VettaChosenCompany(id, name, Value(Role)!) : null;
    }
}
