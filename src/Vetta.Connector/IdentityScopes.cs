namespace Vetta.Connector;

/// <summary>OIDC scopes supported by Vetta. Requested scopes also require client permission on the server.</summary>
public static class VettaIdentityScopes
{
    public const string OpenId = "openid";
    public const string Profile = "profile";
    public const string NationalId = "national_id";
    public static bool IsSupported(string scope) => scope is OpenId or Profile or NationalId;
}

public static class VettaIdentityClaims
{
    /// <summary>Verified Icelandic national ID, released in an ID token only for an authorized national_id scope.</summary>
    public const string NationalId = "national_id";
}
