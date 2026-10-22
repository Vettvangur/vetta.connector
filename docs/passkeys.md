# Passkeys on your own website

Available in **Vetta.Connector 0.2.0** and **Vetta.Connector.AspNetCore 0.2.0**. The corresponding service must be enabled for your application.

Applications default to **Vetta passkeys**, which are used on Vetta's authentication domain. For native passkeys on your website, your Vetta administrator must verify and enable your exact HTTPS origin. Users enroll a separate passkey; existing Vetta passkeys cannot be transferred.

This version supports one hostname per application on HTTPS port 443, without wildcards or parent-domain sharing. For example, `https://accounts.example.com` uses RP ID `accounts.example.com`. Credentials are isolated by application even when applications share a hostname. `PreferPasskeys` controls presentation separately.

## ASP.NET Core setup

Configure `AddVettaConnector` and `AddVettaAuthenticationFlow<LoginCompletion>` as described in the main README, then explicitly enable the new endpoints:

```csharp
builder.Services.AddVettaCustomerPasskeys();
// After building the application:
app.MapVettaAuthentication("/auth/vetta");
```

Successful passkey login calls your existing `IVettaLoginCompletion` with the verified identity. Registration does not sign the user in again. Production implementations of `IVettaAttemptStore` must persist the new `LoginAttempt.Kind` and `LoginAttempt.Origin` fields and keep all transitions atomic across instances. The development store already supports these fields.

From your website's JavaScript module:

```js
import { VettaPasskeys } from "/auth/vetta/client.js";
const passkeys = new VettaPasskeys();
document.querySelector("#passkey-login").addEventListener("click", async () => {
    try {
        const result = await passkeys.authenticate();
        if (result.state === "Completed" && result.redirect) {
            location.assign(result.redirect);
        } else {
            showError("Couldn't sign you in. Try again or choose another method.");
        }
    } catch {
        showError("Passkey sign-in was cancelled or unavailable.");
    }
});
```

`showError` is your own UI function. Keep an explicit alternative-method button. Do not automatically start Auðkenni authentication after a failed passkey attempt.

## Offer enrollment after Auðkenni login

Registration requires a successful Auðkenni login from the same application and browser. Save the opaque local handle returned by `VettaLogin.start` before calling `wait`:

```js
import { VettaLogin, VettaPasskeys } from "/auth/vetta/client.js";
const login = new VettaLogin();
const passkeys = new VettaPasskeys();
const started = await login.start({ method: 2, phoneNumberOrSsn: kennitala });
if (started.state === "Started") {
    const completedHandle = started.handle;
    const completed = await login.wait();
    if (completed.state === "Completed") {
        // Offer enrollment before navigating to completed.redirect.
        addPasskeyButton.hidden = false;
        addPasskeyButton.onclick = async () => {
            try {
                const result = await passkeys.register(completedHandle);
                showMessage(result.state === "Registered" ? "Passkey saved." : "Couldn't save the passkey.");
            } catch {
                showMessage("Passkey setup was cancelled. You can still continue.");
            }
        };
        // Also show a Continue button pointing to completed.redirect.
    }
}
```

`kennitala`, `addPasskeyButton`, and the UI functions belong to your page. The backend resolves the local handle to the verified provider transaction; browser-supplied user IDs or national IDs are never enrollment proof.

Offer enrollment promptly: the provider proof expires two minutes after authentication started. Each proof can start only one enrollment ceremony. If registration expires or is cancelled, a new verified Auðkenni login is needed to enroll; the existing website login remains valid.

The helper requires native WebAuthn JSON conversion APIs. Call it from a user action. Both `authenticate(signal)` and `register(handle, signal)` accept an AbortSignal. Cancellation consumes the pending ceremony and never automatically retries.

## Core client for your own backend endpoints

`AddVettaConnector` registers `IVettaPasskeyClient`:

```csharp
var policy = await passkeys.GetPolicyAsync(cancellationToken);
if (policy is not { Scope: "CustomerDomain", Origin: not null })
    return; // Use the application's Vetta authentication flow or another method.
var result = await passkeys.LoginOptionsAsync(policy.Origin, browserBinding, cancellationToken);
```

Generate `browserBinding` as 32 random bytes encoded as hex. Keep it in a Secure, HttpOnly, host-only cookie or protected backend session. Do not let JavaScript choose it. Store the returned ceremony ID against that browser and send only `OptionsJson` and your own opaque handle to the UI.

Finish with `FinishLoginAsync(origin, binding, ceremonyId, credentialJson)`. Only `Succeeded` with a verified identity may complete login. Enforce CSRF protection, request limits and atomic completion.

For enrollment, call `RegistrationOptionsAsync` with the authentication identifier retained by your backend after successful Auðkenni login, then `FinishRegistrationAsync`. Use `CancelAsync` to abandon an attempt. Never send API keys, bindings, provider proof IDs or verified identities to browser code.

## List and remove passkeys

Management requires a fresh Auðkenni login. Retain the completed local handle as
in the enrollment example. A website cookie or passkey login is not sufficient.

```js
const listed = await passkeys.list(completedHandle);
if (listed.state === "PasskeyList") {
    renderPasskeys(listed.passkeys); // Your UI: id and createdAt; use textContent.
}
// In your Remove button handler:
if (confirm("Remove this passkey? You can still sign in with Auðkenni.")) {
    const result = await passkeys.remove(completedHandle, credentialId);
    if (result.state === "PasskeyList") renderPasskeys(result.passkeys);
    else showError("Authenticate with Auðkenni again before removing a passkey.");
}
```

Each provider proof allows one enrollment or removal and expires two minutes after
provider authentication started. Listing does not consume it. Further removals
require fresh Auðkenni authentication. Removing the last key is allowed because
Auðkenni remains available. Removing a key from Vetta does not delete the entry
from the device's password manager.

Core integrations use `IVettaPasskeyClient.ListAsync` and `RemoveAsync` with the
backend browser binding and recent verified provider transaction ID. Never accept
that transaction ID from browser input. The ASP.NET helper resolves it from the
bound local handle. Listing returns IDs and creation dates, never key material or
verified personal identity.
