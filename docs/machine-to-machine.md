# Machine-to-machine: call and protect your own APIs

Available in **Vetta.Connector 0.4.0** and **Vetta.Connector.AspNetCore 0.4.0**. Your Vetta administrator registers your API and creates a machine client for your application.

Your backend services can get short-lived access tokens from Vetta with the OAuth 2.0 **client-credentials** grant and send them to your own APIs. No user is involved. Your API validates the tokens with Vetta's published signing keys.

Before you start you need:

- the Vetta **authority** (issuer), e.g. `https://api.vetta.is/`;
- your API's **identifier**, e.g. `https://api.example.is`, which becomes the token audience (`aud`);
- the API's **scopes**, e.g. `orders.read`;
- a machine **client ID** (`m2m_…`) and **secret** (`vms_…`). The secret is shown once when the client is created or rotated.

Tokens are signed JWTs that are valid for 10 minutes and contain `iss`, `aud`, `scope` (space-separated), `sub` and `client_id` (both the client ID), and `exp`. There are no refresh tokens.

## Call an API with machine tokens

Add a token handler to the `HttpClient` that calls your API. Each configured client keeps its own cached token, refreshes it about a minute before it expires, and lets only one request refresh at a time.

```csharp
builder.Services.AddHttpClient("orders", client => client.BaseAddress = new Uri("https://api.example.is/"))
    .AddVettaMachineToken(options =>
    {
        options.Authority = new Uri(builder.Configuration["Vetta:Authority"]!);
        options.ClientId = builder.Configuration["Vetta:MachineClientId"]!;
        options.ClientSecret = builder.Configuration["Vetta:MachineClientSecret"]!;
        options.Scopes.Add("orders.read"); // omit to request every scope the client is allowed
    });
```

Every request from that client now carries `Authorization: Bearer <token>`. Options are validated when you register them: the authority must be an absolute HTTPS URL, and the client ID and secret are required.

For other transports (gRPC, message queues), register the provider and request tokens yourself:

```csharp
builder.Services.AddVettaMachineTokenProvider(options => { /* as above */ });

public sealed class Worker(IVettaMachineTokenProvider tokens)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(ct);
        // ...
    }
}
```

If Vetta refuses the request, the call throws `VettaMachineTokenException`. Its `Error` property is the OAuth error (`invalid_client`, `invalid_scope` or `invalid_request`). The message never contains the secret. Timeouts throw `TimeoutException`.

## Protect your API

Validate Vetta tokens with JWT bearer authentication and require scopes per endpoint:

```csharp
builder.Services.AddAuthentication()
    .AddVettaApi("vetta", options =>
    {
        options.Authority = "https://api.vetta.is/";
        options.Audience = "https://api.example.is"; // your API identifier
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("orders.read", policy => policy.AddAuthenticationSchemes("vetta").RequireVettaScope("orders.read"));
});

app.MapGet("/orders", () => ...).RequireAuthorization("orders.read");
```

`AddVettaApi` checks the signature (keys from the authority's discovery document), the issuer, that `aud` contains your identifier, and the expiry (30 seconds of clock skew by default). Claims keep their raw names (`MapInboundClaims = false`), and `User.Identity.Name` is the client ID. `RequireVettaScope` requires every listed scope in the token's space-separated `scope` claim.

## Other platforms

The protocol is standard OAuth 2.0, so any language works:

```bash
curl -s https://api.vetta.is/connect/token \
  -u "$VETTA_CLIENT_ID:$VETTA_CLIENT_SECRET" \
  -d grant_type=client_credentials -d scope=orders.read
```

The response is `{"access_token":"…","token_type":"Bearer","expires_in":600}`. In your API, use your platform's JWT library with the discovery document at `{authority}.well-known/openid-configuration`, and check `iss`, `aud`, `exp` and `scope`.

## Security guidance

- Keep the secret in a server-side secret store. Never put it in browser or mobile code, URLs or logs.
- Request only the scopes a service needs, and give each service its own machine client.
- Cache tokens. The token endpoint is rate-limited (30 requests per minute per IP), and the provider here caches for you.
- Rotate the secret when someone who knew it leaves or it may have leaked. The old secret stops working immediately, so deploy the new one right away. Tokens already issued expire within 10 minutes.
- Do not send an `Origin` header to the token endpoint; browser origins are reserved for single-page apps.
