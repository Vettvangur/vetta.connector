using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Vetta.Connector;
using Vetta.Connector.AspNetCore;

public sealed class OpenIdConnectTests
{
    private static void Configure(VettaOpenIdConnectOptions options)
    {
        options.Authority = "https://identity.example/";
        options.ClientId = "sample-client";
        options.SignInScheme = "external";
    }
    [Fact]
    public void Registration_uses_pkce_raw_claim_names_and_explicit_scopes()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddAuthentication().AddCookie("external").AddVettaOpenIdConnect("vetta", options =>
        { Configure(options); options.Scopes.Add(VettaIdentityScopes.NationalId); });
        using var provider = services.BuildServiceProvider();
        var oidc = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("vetta");
        Assert.True(oidc.UsePkce); Assert.True(oidc.RequireHttpsMetadata);
        Assert.False(oidc.MapInboundClaims); Assert.False(oidc.SaveTokens);
        Assert.False(oidc.GetClaimsFromUserInfoEndpoint);
        Assert.Equal("code", oidc.ResponseType); Assert.Equal("external", oidc.SignInScheme);
        Assert.Equal(new[] { "national_id", "openid", "profile" }, oidc.Scope.Order());
    }
    [Theory]
    [InlineData(null, false)]
    [InlineData("123", false)]
    [InlineData("abcdefghij", false)]
    [InlineData("1234567890", true)]
    public async Task Requested_national_identity_must_be_present_and_valid(string? value, bool valid)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddAuthentication().AddCookie("external").AddVettaOpenIdConnect("vetta", options =>
        { Configure(options); options.Scopes.Add(VettaIdentityScopes.NationalId); });
        using var provider = services.BuildServiceProvider();
        var oidc = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("vetta");
        var identity = new System.Security.Claims.ClaimsIdentity("test");
        if (value is not null) identity.AddClaim(new(VettaIdentityClaims.NationalId, value));
        var context = new TokenValidatedContext(new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            new AuthenticationScheme("vetta", null, typeof(OpenIdConnectHandler)), oidc,
            new System.Security.Claims.ClaimsPrincipal(identity), new AuthenticationProperties());
        await oidc.Events.TokenValidated(context);
        Assert.Equal(!valid, context.Result?.Failure is not null);
        if (valid)
        {
            identity.AddClaim(new(VettaIdentityClaims.NationalId, value!));
            await oidc.Events.TokenValidated(context);
            Assert.NotNull(context.Result?.Failure);
        }
    }
    [Fact]
    public void Company_scope_is_accepted_and_requested()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddAuthentication().AddCookie("external").AddVettaOpenIdConnect("vetta", options =>
        { Configure(options); options.Scopes.Add(VettaCompanyClaims.Scope); });
        using var provider = services.BuildServiceProvider();
        var oidc = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("vetta");
        Assert.Equal(new[] { "company", "national_id", "openid", "profile" }, oidc.Scope.Order());
    }
    [Theory]
    [InlineData("email")][InlineData("offline_access")][InlineData("arbitrary")]
    public void Unknown_scopes_are_rejected(string scope)
    {
        var builder = new ServiceCollection().AddAuthentication();
        Assert.Throws<ArgumentException>(() => builder.AddVettaOpenIdConnect("vetta", options =>
        { Configure(options); options.Scopes.Add(scope); }));
    }
    [Fact]
    public void Name_and_national_identity_are_requested_by_default() =>
        Assert.Equal(new[] { "national_id", "openid", "profile" }, new VettaOpenIdConnectOptions().Scopes.Order());
    [Fact]
    public void Removing_national_identity_requests_only_the_name()
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddAuthentication().AddCookie("external").AddVettaOpenIdConnect("vetta", options =>
        { Configure(options); options.Scopes.Remove(VettaIdentityScopes.NationalId); });
        using var provider = services.BuildServiceProvider();
        var oidc = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("vetta");
        Assert.Equal(new[] { "openid", "profile" }, oidc.Scope.Order());
    }
    [Fact]
    public void Openid_is_required()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddAuthentication().AddVettaOpenIdConnect("vetta", options =>
        { Configure(options); options.Scopes.Clear(); options.Scopes.Add("profile"); }));
    }
    [Fact]
    public void Insecure_authority_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddAuthentication().AddVettaOpenIdConnect("vetta", options =>
        { Configure(options); options.Authority = "http://identity.example/"; }));
    }
}
