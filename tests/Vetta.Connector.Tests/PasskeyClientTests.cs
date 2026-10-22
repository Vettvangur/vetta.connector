using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
namespace Vetta.Connector.Tests;

public sealed class PasskeyClientTests
{
    [Fact]
    public async Task Registration_sends_backend_binding_proof_and_configured_application()
    {
        var proof = Guid.NewGuid(); var ceremony = Guid.NewGuid();
        var client = Create(async request =>
        {
            Assert.Equal("/passkeys/register-options", request.RequestUri!.AbsolutePath);
            Assert.Equal("apiKey fixture-key", request.Headers.Authorization!.ToString());
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("fixture",json.RootElement.GetProperty("requestingApplication").GetString());
            Assert.Equal(proof,json.RootElement.GetProperty("proofId").GetGuid());
            Assert.Equal(new string('A',64),json.RootElement.GetProperty("browserBinding").GetString());
            return new {state="Options",ceremonyId=ceremony,optionsJson="{}"};
        });
        Assert.Equal(ceremony,(await client.RegistrationOptionsAsync("https://customer.example",new string('A',64),proof)).CeremonyId);
    }
    [Theory]
    [InlineData("https://evil.example/path")]
    [InlineData("http://customer.example")]
    [InlineData("https://customer.example:8443")]
    public async Task Invalid_origins_are_not_sent(string origin)
    {
        var client=Create(_=>throw new Exception("Must not send"));
        await Assert.ThrowsAsync<ArgumentException>(()=>client.LoginOptionsAsync(origin,new string('A',64)));
    }
    [Fact]
    public async Task Success_without_identity_and_success_during_options_are_rejected()
    {
        var client=Create(_=>Task.FromResult<object>(new {state="Succeeded"}));
        Assert.Equal("Unavailable",(await client.FinishLoginAsync("https://customer.example",new string('A',64),Guid.NewGuid(),"{}")).State);
        Assert.Equal("Unavailable",(await client.LoginOptionsAsync("https://customer.example",new string('A',64))).State);
    }
    [Fact]
    public async Task Shared_policy_is_distinct_from_customer_domain_policy()
    {
        var client=Create(_=>Task.FromResult<object>(new {state="Policy",scope="Vetta",preferPasskeys=true}));
        var result=await client.GetPolicyAsync();
        Assert.Equal("Vetta",result!.Scope); Assert.Null(result.Origin); Assert.True(result.PreferPasskeys);
    }
    private static VettaPasskeyClient Create(Func<HttpRequestMessage,Task<object>> action) =>
        new(new HttpClient(new Handler(action)),Options.Create(new VettaConnectorOptions
        {BaseUrl=new("https://api.example/"),ApplicationId="fixture",ApiKey="fixture-key"}));
    private sealed class Handler(Func<HttpRequestMessage,Task<object>> action):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) =>
            new(HttpStatusCode.OK){Content=JsonContent.Create(await action(request))};
    }
}
