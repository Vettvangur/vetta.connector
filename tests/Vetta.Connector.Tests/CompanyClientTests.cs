using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Vetta.Connector;

public sealed class CompanyClientTests
{
    private const string Session = "c2Vzc2lvbi10b2tlbi1mb3ItdGVzdGluZy0wMTIzNDU2Nzg";
    private const string Choices = """{"companies":[{"nationalId":"5501692829","name":"Örn ehf.","role":"procuration_holder","canManage":true,"expiresUtc":null}],"invitations":[{"id":"11111111-1111-4111-8111-111111111111","nationalId":"4202691299","name":"Nýja ehf.","role":"member","invitedBy":"Jón Jónsson","expiresUtc":"2027-01-01T00:00:00Z"}],"registryUnavailable":false}""";

    [Fact]
    public async Task Opening_a_session_sends_the_key_and_the_sign_in_and_reads_the_choices()
    {
        HttpRequestMessage? sent = null; string? body = null;
        var client = Client(async request =>
        {
            sent = request; body = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, $$"""{"session":"{{Session}}","expiresUtc":"2026-11-23T20:00:00Z","person":{"name":"Jón Jónsson"},"companies":{{Choices}}}""");
        });
        var identifier = Guid.NewGuid();
        var result = await client.OpenSessionAsync(identifier);

        Assert.True(result.Succeeded);
        Assert.Equal((Session, "Jón Jónsson"), (result.Value!.Token, result.Value.PersonName));
        var company = Assert.Single(result.Value.Choices.Companies);
        Assert.Equal(("5501692829", VettaCompanyRoles.ProcurationHolder, true), (company.NationalId, company.Role, company.CanManage));
        Assert.Equal("Jón Jónsson", Assert.Single(result.Value.Choices.Invitations).InvitedBy);
        Assert.Equal("https://vetta.example/company/v1/session", sent!.RequestUri!.AbsoluteUri);
        Assert.Equal(("apikey", "fixture-key"), (sent.Headers.Authorization!.Scheme, sent.Headers.Authorization.Parameter));
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(("fixture-app", identifier), (json.RootElement.GetProperty("requestingApplication").GetString(), json.RootElement.GetProperty("identifier").GetGuid()));
    }

    [Fact]
    public async Task Choosing_returns_the_confirmed_company_and_sends_the_session_and_kennitala()
    {
        string? body = null;
        var client = Client(async request => { body = await request.Content!.ReadAsStringAsync(); return Json(HttpStatusCode.OK, """{"company":{"id":"5501692829","name":"Örn ehf.","role":"manager"}}"""); });
        var chosen = await client.ChooseAsync(Session, "550169-2829");
        Assert.Equal(new VettaChosenCompany("5501692829", "Örn ehf.", VettaCompanyRoles.Manager), chosen.Value);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal((Session, "5501692829"), (json.RootElement.GetProperty("session").GetString(), json.RootElement.GetProperty("company").GetString()));
    }

    [Fact]
    public async Task Inviting_sends_utc_and_reads_a_large_access_list()
    {
        string? body = null;
        var people = string.Join(",", Enumerable.Range(0, 450).Select(i =>
            $$"""{"accessId":"{{Guid.NewGuid()}}","name":"Person {{i}} with a fairly long name","nationalId":"010130-****","role":"member","status":"Active","expiresUtc":null,"invitedUtc":"2026-11-23T10:00:00Z","invitedBy":"Jón Jónsson","revision":"{{Guid.NewGuid()}}","isYou":false,"removable":true}"""));
        var answer = $$$"""{"access":{"nationalId":"5501692829","name":"Örn ehf.","yourRole":"procuration_holder","people":[{{{people}}}]}}""";
        Assert.True(answer.Length > 65536);
        var client = Client(async request => { body = await request.Content!.ReadAsStringAsync(); return Json(HttpStatusCode.OK, answer); });
        var result = await client.InviteAsync(Session, "5501692829", "111111-1119", VettaCompanyRoles.Member, new DateTime(2027, 1, 31, 0, 0, 0, DateTimeKind.Unspecified), "anna@example.is");
        Assert.Equal(450, result.Value!.People.Count);
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(("1111111119", "member", "anna@example.is"), (json.RootElement.GetProperty("invitee").GetString(), json.RootElement.GetProperty("role").GetString(), json.RootElement.GetProperty("email").GetString()));
        Assert.Equal(new DateTime(2027, 1, 31, 0, 0, 0, DateTimeKind.Utc), json.RootElement.GetProperty("expiresUtc").GetDateTime().ToUniversalTime());
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"addon_disabled","message":"Company sign-in is not switched on for this organisation."}""", "addon_disabled", "Company sign-in is not switched on for this organisation.")]
    [InlineData(HttpStatusCode.Conflict, """{"error":"company_conflict","message":"This person already has access to the company. Change their access instead."}""", "company_conflict", "This person already has access to the company. Change their access instead.")]
    [InlineData(HttpStatusCode.Unauthorized, "", "unauthorized", null)]
    [InlineData(HttpStatusCode.BadGateway, """{"error":"<script>","message":null}""", "server_unavailable", null)]
    public async Task Refusals_carry_vettas_code_and_message(HttpStatusCode status, string answer, string error, string? message)
    {
        var result = await Client(_ => Task.FromResult(Json(status, answer))).GetChoicesAsync(Session);
        Assert.False(result.Succeeded);
        Assert.Equal((error, message, (int)status), (result.Error, result.Message, result.HttpStatusCode));
    }

    [Fact]
    public async Task Remove_answer_and_transport_failures()
    {
        Assert.True((await Client(_ => Task.FromResult(Json(HttpStatusCode.OK, """{"removed":true}"""))).RemoveAccessAsync(Session, Guid.NewGuid(), Guid.NewGuid())).Value);
        Assert.Single((await Client(_ => Task.FromResult(Json(HttpStatusCode.OK, """{"companies":""" + Choices + "}"))).AnswerInvitationAsync(Session, Guid.NewGuid(), true)).Value!.Companies);
        Assert.Equal("transport_failure", (await Client(_ => throw new HttpRequestException("down")).GetChoicesAsync(Session)).Error);
        Assert.Equal("invalid_response", (await Client(_ => Task.FromResult(Json(HttpStatusCode.OK, "{}"))).ChooseAsync(Session, "5501692829")).Error);
        Assert.Equal("invalid_response", (await Client(_ => Task.FromResult(Json(HttpStatusCode.OK, "not json"))).GetChoicesAsync(Session)).Error);
    }

    [Fact]
    public async Task Bad_input_is_refused_before_any_request()
    {
        var calls = 0;
        var client = Client(_ => { calls++; return Task.FromResult(Json(HttpStatusCode.OK, "{}")); });
        await Assert.ThrowsAsync<ArgumentException>(() => client.OpenSessionAsync(Guid.Empty));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetChoicesAsync("short"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetChoicesAsync(Session + "/../x"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ChooseAsync(Session, "12345"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.InviteAsync(Session, "5501692829", "1111111119", "owner"));
        Assert.Equal(0, calls);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static VettaCompanyClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(new HttpClient(new Handler(send)), Options.Create(new VettaConnectorOptions { BaseUrl = new("https://vetta.example/"), ApplicationId = "fixture-app", ApiKey = "fixture-key" }));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
}

public sealed class CompanyClaimsTests
{
    [Fact]
    public void The_company_is_read_from_the_tokens_claims_only_when_complete()
    {
        static System.Security.Claims.ClaimsPrincipal With(params (string Type, string Value)[] claims) =>
            new(new System.Security.Claims.ClaimsIdentity(claims.Select(x => new System.Security.Claims.Claim(x.Type, x.Value)), "test"));
        Assert.Equal(new VettaChosenCompany("5501692829", "Örn ehf.", "manager"),
            VettaCompanyClaims.From(With(("sub", "x"), ("company_id", "5501692829"), ("company_name", "Örn ehf."), ("company_role", "manager"))));
        Assert.Null(VettaCompanyClaims.From(With(("sub", "x"))));
        Assert.Null(VettaCompanyClaims.From(With(("company_id", "5501692829"), ("company_name", "Örn ehf."), ("company_role", "owner"))));
        Assert.Null(VettaCompanyClaims.From(With(("company_id", "55016"), ("company_name", "Örn ehf."), ("company_role", "member"))));
        Assert.Null(VettaCompanyClaims.From(null));
    }
}
