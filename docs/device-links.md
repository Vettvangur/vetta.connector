# QR and mobile app sign-in

Available in the upcoming **0.3.0** release. These flows run on your own website.
Use a source project reference until that version is published.

A desktop user scans a rotating QR code in the Auðkenni app. A mobile user opens
that app from a link and returns to your website after approval. Neither flow
requires the visitor to enter a kennitala. Your backend receives the verified
identity after successful authentication.

## Enable the feature

Ask Vetta to enable device links for your application and register your **exact
HTTPS callback URL**, including its path and port. For the default endpoint prefix:

```text
https://your-site.example/auth/vetta/device-link/callback
```

Do not add a query string or fragment. Register development and production URLs
separately. The callback must be reachable from the device being used. A phone
cannot open your development computer's `localhost` address.

## ASP.NET Core setup

Install `Vetta.Connector.AspNetCore`, and optionally `Vetta.Connector.SqlServer` for
persistent SQL Server storage across instances. Configure the regular connector and your
`IVettaLoginCompletion` implementation as described in the README, then add:

```csharp
using Vetta.Connector.AspNetCore;
using Vetta.Connector.SqlServer;

builder.Services.AddVettaAuthenticationFlow<MyLoginCompletion>();
builder.Services.AddVettaDeviceLinks(new()
{
    CallbackUrl = new Uri("https://your-site.example/auth/vetta/device-link/callback"),
    ReturnPath = "/sign-in" // Local recovery page when the mobile callback fails.
});
builder.Services.AddVettaSqlServerAttemptStore(
    builder.Configuration.GetConnectionString("VettaAttempts")
    ?? throw new InvalidOperationException("Configure the attempt database."));

// After authentication middleware:
app.MapVettaAuthentication();
```

Apply the [SQL Server migration](attempt-storage.md) to your website database before starting the application.
For reverse proxies, configure trusted forwarded headers before these endpoints;
the request must retain its public HTTPS scheme and host. If you change the mapped
prefix, change the registered callback path to match.

`MyLoginCompletion` resolves your local account, enforces account policy, signs in
using your cookie scheme, and returns a local URL such as `/account`. Use its
`completionId` for idempotency when recording side effects. Never sign a visitor in
based on a callback query, QR scan, or browser-supplied identity alone.

## Browser code

Serve this code from your own JavaScript file:

```js
import { VettaDeviceLinks } from '/auth/vetta/client.js';
const login = new VettaDeviceLinks();
let stopQr = () => {};

// Run in response to a click, never automatically on page load.
async function showQr() {
    const start = await login.startQr();
    if (start.state !== 'Started') return showError(start.errorCode);
    qrImage.hidden = false;
    stopQr = login.refreshQr(qrImage, start.qrImageUrl, () => {
        showError('qr_refresh_failed'); // Offer Cancel and a fresh attempt.
    });
    try {
        const result = await login.wait();
        if (result.state === 'Completed') location.assign(result.redirect);
        else showError(result.errorCode);
    } finally { stopQr(); }
}

async function prepareMobile() {
    const start = await login.startWeb2App();
    if (start.state !== 'Started') return showError(start.errorCode);
    // A separate visible anchor gives the browser a direct user gesture to open the app.
    openAppLink.href = /Android/i.test(navigator.userAgent)
        ? start.androidAppLink : start.appLink;
    openAppLink.hidden = false;
    const result = await login.wait();
    if (result.state === 'Completed') location.assign(result.redirect);
    else showError(result.errorCode);
}

async function cancel() {
    const result = await login.cancel();
    if (result.state === 'Cancelled' || result.state === 'Expired') stopQr();
    // Busy means completion is already underway; do not start a second attempt.
}
addEventListener('pagehide', () => { stopQr(); login.abort?.abort(); });
```

Surround calls with error handling and disable start buttons while an attempt is
active. A complete example with controls and recovery messages is in
[`CustomLogin.Web`](../samples/CustomLogin.Web/wwwroot/device-link.html).
The helper polls at one-second intervals and stops after five minutes. QR refresh
starts only when requested and waits for the previous image to load before refreshing.
Three consecutive image failures stop QR refresh; status network errors stop polling; offer cancellation before a new attempt.

The helper creates a secure HttpOnly browser-binding cookie and protects POST
requests with antiforgery validation. Mobile returns use a separate HttpOnly
attempt cookie and `SameSite=Lax` to permit top-level navigation from the app.
The callback must return to **the same browser/profile** that started the request.
Only one mobile attempt is retained in that browser's callback cookie. A new start
cancels a previous idle mobile attempt; it cannot interrupt a claimed completion.

The mobile callback completes authentication and redirects to the local URL returned
by your completion handler. Failed callbacks return to `ReturnPath` with
`vettaDevice=failed`. Do not expose or log the callback query: it contains short-lived
authentication material. Configure proxy/access logs to omit it too.

Cancellation revokes the local attempt and stops the website flow. The upstream
API has no device-link cancellation operation; the provider request expires on its
own. Starting again may incur another provider request.

## Core client without the ASP.NET endpoints

`AddVettaConnector` also registers `IVettaDeviceLinkClient`:

```csharp
var start = await deviceLinks.StartAsync(
    new(DeviceLinkMethod.Qr, Message: "Sign in to Example"), cancellationToken);
if (start.Authentication.State != AuthenticationState.Started)
    return; // Map Authentication.Error and ErrorCode to your UI.
var id = start.Authentication.Identifier!.Value;
var qr = await deviceLinks.GetQrAsync(id, cancellationToken);
var status = await deviceLinks.GetStatusAsync(id, cancellationToken);
// Or use WaitForStatusAsync for a bounded long poll.
```

For mobile:

```csharp
var start = await deviceLinks.StartAsync(new(
    DeviceLinkMethod.Web2App,
    CallbackUrl: new Uri("https://your-site.example/callback")), cancellationToken);
// Store start.Authentication.Identifier against a cryptographically random,
// HttpOnly browser-bound attempt. Display start.AppLink or AndroidAppLink.

// In your callback, resolve id from that browser binding, never from request input:
var result = await deviceLinks.CompleteCallbackAsync(
    storedAuthenticationId, fullCallbackUri, cancellationToken);
```

Pass the complete callback URI without rebuilding or interpreting its query fields.
Verify its origin/path against your fixed configured callback. Implement expiry,
antiforgery for starts, atomic single-use completion, and local redirect validation.
The ASP.NET package supplies these behaviors. `Message` has a 60-character limit;
`RestrictToSsn` optionally restricts a start to a particular ten-digit kennitala.

No start or callback is automatically retried. A timeout can mean the provider
accepted the request before the response was lost; do not blindly resend it.

## Safe errors

`AuthenticationResult.ErrorCode`, `DeviceLinkQrResult.ErrorCode`, and
`PasskeyResponse.ErrorCode` preserve recognized public codes. HTTP error responses
also expose `HttpStatusCode` to the backend. ASP.NET browser replies carry only the
safe code, never provider error bodies, credentials, or identity details.

| Code | Suggested UI handling |
| --- | --- |
| `user_not_found` | Explain that no Auðkenni account was found; offer another method. |
| `rejected` | Explain that approval was declined; offer a fresh attempt. |
| `timeout` | Offer a new attempt. |
| `credential_blocked`, `credential_unavailable` | Offer another method. |
| `wrong_challenge` | Explain the verification code did not match. |
| `request_in_progress` | Ask the user to finish the existing request. |
| `provider_unavailable`, `provider_timeout` | Show a temporary service error. |
| `invalid_callback`, `ended`, `unknown_login` | Start a fresh browser-bound attempt. |

Unknown codes are intentionally omitted; always provide a friendly generic fallback.
A missing code does not imply success. Inspect the result state before using an identity.
Log the safe code, HTTP status, and your own correlation ID. Keep API keys, callback
queries, app/QR links, kennitalas, and full provider responses out of logs.

## Run the complete example

Configure the base URL, application ID, and API key using the README instructions.
Enable device links for that application, register the callback below, and run:

```sh
dotnet user-secrets set "Vetta:DeviceLinkCallbackUrl" "https://localhost:7243/auth/vetta/device-link/callback" --project samples/CustomLogin.Web
dotnet run --project samples/CustomLogin.Web --launch-profile https
```

Open `https://localhost:7243/device-link.html`. For an actual phone-to-website return,
use a trusted HTTPS development domain reachable from the phone, listen on that
address, and register its exact callback instead of localhost. If testing persistent
storage, also set `ConnectionStrings:VettaAttempts` to your test website database connection string and apply the migration first.
