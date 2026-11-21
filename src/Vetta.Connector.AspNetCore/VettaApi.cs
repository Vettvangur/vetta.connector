using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Vetta.Connector.AspNetCore;

/// <summary>Protect your own API with Vetta machine tokens (client-credentials grant).</summary>
public sealed class VettaApiOptions
{
    /// <summary>The Vetta issuer, e.g. <c>https://api.vetta.is/</c>. Signing keys come from its discovery document.</summary>
    public string Authority { get; set; } = "";
    /// <summary>Your API's identifier as registered in Vetta; tokens must contain it in <c>aud</c>.</summary>
    public string Audience { get; set; } = "";
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}

public static class VettaApiRegistration
{
    /// <summary>The space-separated scope claim in Vetta access tokens.</summary>
    public const string ScopeClaim = "scope";

    /// <summary>Register JWT bearer validation for Vetta machine tokens: issuer, signature, audience and lifetime.</summary>
    public static AuthenticationBuilder AddVettaApi(this AuthenticationBuilder builder, string scheme, Action<VettaApiOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new VettaApiOptions();
        configure(options);
        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authority) || authority.Scheme != "https" ||
            authority.UserInfo.Length != 0 || authority.Query.Length != 0 || authority.Fragment.Length != 0)
            throw new ArgumentException("Vetta authority must be an absolute HTTPS URL.");
        if (string.IsNullOrWhiteSpace(options.Audience)) throw new ArgumentException("Your API identifier (audience) is required.");
        if (options.ClockSkew < TimeSpan.Zero || options.ClockSkew > TimeSpan.FromMinutes(5)) throw new ArgumentException("ClockSkew must be between zero and five minutes.");
        return builder.AddJwtBearer(scheme, jwt =>
        {
            jwt.Authority = authority.AbsoluteUri;
            jwt.Audience = options.Audience;
            jwt.RequireHttpsMetadata = true;
            jwt.MapInboundClaims = false;
            jwt.SaveToken = false;
            jwt.TokenValidationParameters.ValidateIssuer = true;
            jwt.TokenValidationParameters.ValidateAudience = true;
            jwt.TokenValidationParameters.ValidAudience = options.Audience;
            jwt.TokenValidationParameters.ValidateLifetime = true;
            jwt.TokenValidationParameters.RequireExpirationTime = true;
            jwt.TokenValidationParameters.RequireSignedTokens = true;
            jwt.TokenValidationParameters.ClockSkew = options.ClockSkew;
            jwt.TokenValidationParameters.NameClaimType = "client_id";
        });
    }

    /// <summary>Require every listed scope in the token's space-separated <c>scope</c> claim.</summary>
    public static AuthorizationPolicyBuilder RequireVettaScope(this AuthorizationPolicyBuilder builder, params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (scopes is not { Length: > 0 } || scopes.Any(s => string.IsNullOrWhiteSpace(s) || s.Any(char.IsWhiteSpace)))
            throw new ArgumentException("Name at least one scope without whitespace.");
        return builder.RequireAuthenticatedUser().RequireAssertion(context =>
        {
            var granted = context.User.FindAll(ScopeClaim).SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
            return scopes.All(granted.Contains);
        });
    }
}
