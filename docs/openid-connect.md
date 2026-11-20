# Optional hosted OpenID Connect login

The direct connector start/status API continues to work unchanged. Hosted OIDC is
an alternative integration using standard ASP.NET Core middleware and Vetta's
hosted login page. Calling `AddVettaOpenIdConnect` is optional and does not modify
existing direct endpoints. It requires an OIDC client registered by the Vetta
operator with exact callback URLs; a direct API key is not an OIDC registration.

```csharp
using Vetta.Connector;
using Vetta.Connector.AspNetCore;

builder.Services.AddAuthentication()
    .AddCookie("external", options => options.ExpireTimeSpan = TimeSpan.FromMinutes(5))
    .AddVettaOpenIdConnect("vetta", options =>
    {
        options.Authority = "https://your-vetta-api.example/";
        options.ClientId = "sample-client";
        options.SignInScheme = "external";
        options.CallbackPath = "/signin-vetta";
        options.Scopes.Add(VettaIdentityScopes.NationalId);
    });
```

The URL and client above are placeholders. The registered callback is the site's
HTTPS origin plus `/signin-vetta`. Use your host's existing external cookie scheme
when one exists. Install `Vetta.Connector.AspNetCore` and enable the host's normal
authentication/authorization middleware. Start with an explicit `Challenge` using
scheme `vetta` and a fixed local completion URL in `AuthenticationProperties.RedirectUri`.
The middleware owns `/signin-vetta`; use a different path for account completion.

At the completion endpoint, authenticate the external cookie, enforce your account
policy, issue your normal application cookie and clear the temporary external
cookie. Read `VettaIdentityClaims.NationalId` only from the validated authenticated
principal. Never take the account-matching identifier from query strings, submitted
form data, or a JWT decoded without signature/issuer/audience/lifetime validation.
Reject missing identity and blocked accounts. Do not silently create or merge
accounts by name; choose an explicit account-linking/provisioning policy.

| Requested scope | Data |
| --- | --- |
| `openid` | Required; opaque `sub` |
| `profile` | Verified `name` |
| `national_id` | Verified Icelandic kennitala in the ID token |

Defaults request `openid profile`. To request only the identifier, remove
`VettaIdentityScopes.Profile` from `options.Scopes`. `national_id` must be enabled
for this OIDC client by the server operator as well as requested by the client.
A direct connector API key does not grant this permission. The helper fails login
if a requested national ID is missing, duplicated or malformed. It does not log
or persist the token. The host controls its cookie and account data retention.

Authorization code + S256 PKCE, HTTPS metadata, standard token validation and raw
claim names are used. The helper does not save tokens or call UserInfo. There is
no refresh-token/offline-access support, email or phone scope. For advanced event
handling, use the standard ASP.NET Core named OIDC options; preserve identity
validation when replacing token-validation events. The existing `VerifiedIdentity.Ssn`
on the direct API retains its existing contract.
