using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Vetta.Connector.AspNetCore;

/// <summary>Optional hosted OIDC login configuration, separate from the direct start/status API.</summary>
public sealed class VettaOpenIdConnectOptions
{
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    /// <summary>The host's external/temporary cookie scheme. Apply account policy before issuing the application cookie.</summary>
    public string SignInScheme { get; set; } = "";
    public string CallbackPath { get; set; } = "/signin-vetta";
    /// <summary>
    /// Requested scopes. By default the verified name and kennitala (<c>openid profile national_id</c>), which new Vetta
    /// sign-in clients allow. Remove <see cref="VettaIdentityScopes.NationalId"/> to receive only the name.
    /// </summary>
    public ICollection<string> Scopes { get; } = new List<string> { VettaIdentityScopes.OpenId, VettaIdentityScopes.Profile, VettaIdentityScopes.NationalId };
}

public static class VettaOpenIdConnectRegistration
{
    /// <summary>Register standard authorization-code + PKCE middleware for a registered public Vetta client.</summary>
    public static AuthenticationBuilder AddVettaOpenIdConnect(this AuthenticationBuilder builder,
        string scheme, Action<VettaOpenIdConnectOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new VettaOpenIdConnectOptions();
        configure(options);
        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) || authority.Scheme != "https" ||
            authority.UserInfo.Length != 0 || authority.Query.Length != 0 || authority.Fragment.Length != 0)
            throw new ArgumentException("Vetta authority must be an absolute HTTPS URL.");
        if (string.IsNullOrWhiteSpace(options.ClientId) || string.IsNullOrWhiteSpace(options.SignInScheme) || options.SignInScheme == scheme)
            throw new ArgumentException("A registered client ID and a separate sign-in cookie scheme are required.");
        if (!options.CallbackPath.StartsWith('/') || options.CallbackPath.StartsWith("//") ||
            options.CallbackPath.Any(c => char.IsControl(c) || c is '?' or '#' or '\\'))
            throw new ArgumentException("CallbackPath must be a local absolute path without query or fragment.");
        if (!options.Scopes.Contains(VettaIdentityScopes.OpenId) || options.Scopes.Any(x => !VettaIdentityScopes.IsSupported(x)))
            throw new ArgumentException("Select supported Vetta identity scopes, including openid.");
        return builder.AddOpenIdConnect(scheme, oidc =>
        {
            oidc.Authority = authority.AbsoluteUri;
            oidc.ClientId = options.ClientId;
            oidc.SignInScheme = options.SignInScheme;
            oidc.CallbackPath = options.CallbackPath;
            oidc.ResponseType = OpenIdConnectResponseType.Code;
            oidc.UsePkce = true;
            oidc.RequireHttpsMetadata = true;
            oidc.MapInboundClaims = false;
            oidc.SaveTokens = false;
            oidc.GetClaimsFromUserInfoEndpoint = false;
            oidc.TokenValidationParameters.NameClaimType = "name";
            oidc.Scope.Clear();
            foreach (var scope in options.Scopes.Distinct(StringComparer.Ordinal)) oidc.Scope.Add(scope);
            oidc.Events.OnTokenValidated = context =>
            {
                // A requested identity used for account matching must never silently disappear.
                if (options.Scopes.Contains(VettaIdentityScopes.NationalId))
                {
                    var values = context.Principal?.FindAll(VettaIdentityClaims.NationalId).Select(x => x.Value).ToArray() ?? [];
                    if (values.Length != 1 || values[0].Length != 10 || values[0].Any(c => c is < '0' or > '9'))
                        context.Fail("The requested verified national identity is unavailable.");
                }
                return Task.CompletedTask;
            };
        });
    }
}
