# Vetta Connector for .NET

Add Auðkenni authentication to your website while keeping your own login page,
accounts, and application sessions. Vetta Connector handles authentication requests
and returns a verified identity to your backend.

Supports **.NET 10** and the following provider authentication methods:

| Method | C# value | User input |
| --- | --- | --- |
| Auðkenni app | `AuthenticationMethod.App` | Icelandic kennitala (10 digits) |
| Electronic ID on a phone SIM | `AuthenticationMethod.Sim` | Icelandic phone number (7 digits) |

Customer-domain passkeys are available from version 0.2.0. See [passkey setup and examples](https://github.com/Vettvangur/vetta.connector/blob/main/docs/passkeys.md). They require a verified domain and explicit enablement; applications default to shared Vetta passkeys.

## Choose your integration

| Library | Use it when |
| --- | --- |
| `Vetta.Connector` | You want a typed HTTP client and already manage your login endpoints and authentication state. |
| `Vetta.Connector.AspNetCore` | You want ready-made ASP.NET Core endpoints, browser binding, antiforgery protection, and a small JavaScript helper for your own UI. Includes the core client. |

The ASP.NET Core helper includes an **in-memory development store**. Before using
that helper in production, supply an `IVettaAttemptStore` implementation suitable
for your deployment. See [production setup](#production-setup).

## Before you start

Obtain these values from Vetta for your application:

- **Base URL:** the HTTPS API address, including its trailing `/`.
- **Application ID:** identifies your registered website/application.
- **API key:** authenticates requests from your backend.

Your website must use HTTPS. Keep the API key in backend configuration; never put
it in JavaScript, HTML, a mobile bundle, or a public repository.

## Installation

Install the ASP.NET Core integration:

```sh
dotnet add package Vetta.Connector.AspNetCore
```

Or install only the core HTTP client:

```sh
dotnet add package Vetta.Connector
```

Release availability is listed on the [GitHub releases page](https://github.com/Vettvangur/vetta.connector/releases).

If you have the connector source locally, add a project reference from your web
project instead:

```sh
# ASP.NET Core endpoints and browser helper (includes the core client)
dotnet add reference /path/to/vetta.connector/src/Vetta.Connector.AspNetCore/Vetta.Connector.AspNetCore.csproj

# Alternatively, use only the core HTTP client
dotnet add reference /path/to/vetta.connector/src/Vetta.Connector/Vetta.Connector.csproj
```

Choose one of the references above.

## 1. Configure the client

For local development, run these commands in **your website project directory**,
replacing the example values with the ones supplied by Vetta:

```sh
dotnet user-secrets init
dotnet user-secrets set "Vetta:BaseUrl" "https://your-vetta-api.example/"
dotnet user-secrets set "Vetta:ApplicationId" "your-application-id"
dotnet user-secrets set "Vetta:ApiKey" "your-api-key"
```

The URL above is a placeholder, not a working service address. In production, use
your deployment's secret configuration. Environment variable equivalents are
`Vetta__BaseUrl`, `Vetta__ApplicationId`, and `Vetta__ApiKey`.

Register the client in `Program.cs`, before `builder.Build()`:

```csharp
using Vetta.Connector;

builder.Services.AddVettaConnector(options =>
{
    options.BaseUrl = new Uri(builder.Configuration["Vetta:BaseUrl"]!);
    options.ApplicationId = builder.Configuration["Vetta:ApplicationId"]!;
    options.ApiKey = builder.Configuration["Vetta:ApiKey"]!;
    options.Language = "is"; // Use "en" for English.
    options.RequestTimeout = TimeSpan.FromSeconds(100);
});
```

Configuration is validated on startup. The default request timeout is 100 seconds;
you can configure a positive duration up to five minutes. Requests are not
automatically retried.

## 2. Add ASP.NET Core login endpoints

Skip to [using the core client directly](#using-the-core-client-directly) if you
already manage your own login flow.

Register your completion handler alongside your website's existing authentication
and authorization services:

```csharp
using Vetta.Connector.AspNetCore;

builder.Services.AddVettaAuthenticationFlow<WebsiteLoginCompletion>();
```

Map the endpoints after configuring your application middleware:

```csharp
var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapVettaAuthentication("/auth/vetta");
// Map your website's other endpoints here.

app.Run();
```

This adds the following routes on **your website**:

| Method | Path | Purpose |
| --- | --- | --- |
| GET | `/auth/vetta/session` | Establish browser binding and obtain an antiforgery token. |
| POST | `/auth/vetta/start` | Start an app or phone authentication request. |
| POST | `/auth/vetta/status` | Check the current attempt and complete website sign-in after verification. |
| POST | `/auth/vetta/cancel` | Cancel the local attempt if completion has not already been claimed. |
| GET | `/auth/vetta/client.js` | JavaScript module for these endpoints. |

The JavaScript helper handles the session and antiforgery token automatically.
POSTs use the `X-Vetta-CSRF` header. Registration sets this header name on the host's
ASP.NET Core antiforgery options, so coordinate it with existing forms if they use
a different header. Map one Vetta flow per website host.

### Connect the verified identity to your accounts

Implement `IVettaLoginCompletion`. It runs after identity verification, on your
backend. Resolve your own account, enforce your access policy, and issue your
website's normal session. Return a local URL such as `/account`.

This example delegates account policy and cookie/session creation to an
application-owned service:

```csharp
using Microsoft.AspNetCore.Http;
using Vetta.Connector;
using Vetta.Connector.AspNetCore;

public sealed class WebsiteLoginCompletion(IWebsiteSignIn signIn)
    : IVettaLoginCompletion
{
    public async Task<string> CompleteAsync(
        HttpContext context,
        VerifiedIdentity identity,
        string completionId,
        CancellationToken cancellationToken)
    {
        await signIn.SignInVerifiedUserAsync(
            context, identity.Ssn, identity.Name,
            completionId, cancellationToken);

        return "/account";
    }
}

// Your application's contract, not a Vetta SDK interface.
public interface IWebsiteSignIn
{
    Task SignInVerifiedUserAsync(
        HttpContext context,
        string kennitala,
        string verifiedName,
        string completionId,
        CancellationToken cancellationToken);
}
```

Implement and register `IWebsiteSignIn` with your account system. Its implementation
should look up the account by the verified identifier, reject accounts that are not
allowed to sign in, and create a session using your internal account ID. A verified
identity alone does not grant roles or permission to access your website.

Use `completionId` as an idempotency key for account creation or other side effects.
Completion is claimed once and is not automatically retried after an interruption.
A lost sign-in response may require a fresh login; retrieving a cached completion
result does not reissue a lost authentication cookie.

For a runnable demonstration of cookie authentication, see the
[custom login sample](samples/CustomLogin.Web/Program.cs). Its account policy is
intentionally minimal; replace it with your website's policy before deployment.

## 3. Connect your login page

Import the JavaScript module from your own mapped endpoint. No frontend framework
is required. This example assumes your page has a form with ID `login-form`, a
`kennitala` input, a `sign-in` button, a `cancel` button, a `check-again` button, and a `status` element
with `role="status"`.

```html
<form id="login-form">
  <label for="kennitala">Kennitala</label>
  <input id="kennitala" inputmode="numeric" autocomplete="off" required>
  <button id="sign-in" type="submit">Sign in with Auðkenni</button>
</form>
<button id="cancel" type="button" disabled>Cancel</button>
<button id="check-again" type="button" disabled>Check again</button>
<p id="status" role="status"></p>

<script type="module">
import { VettaLogin } from '/auth/vetta/client.js';

const login = new VettaLogin('/auth/vetta');
const form = document.querySelector('#login-form');
const input = document.querySelector('#kennitala');
const start = document.querySelector('#sign-in');
const cancel = document.querySelector('#cancel');
const checkAgain = document.querySelector('#check-again');
const status = document.querySelector('#status');

function updateButtons() {
    start.disabled = login.running || Boolean(login.handle);
    cancel.disabled = !login.handle;
    checkAgain.disabled = login.running || !login.handle;
}

async function checkStatus() {
    checkAgain.disabled = true;
    try {
        const finished = await login.wait();
        if (finished.state === 'Completed') {
            window.location.assign(finished.redirect);
            return;
        }
        status.textContent = ['Busy', 'Unavailable'].includes(finished.state)
            ? 'Sign-in is interrupted. Check again or cancel this attempt.'
            : 'Sign-in did not complete. You can start again.';
    } finally {
        updateButtons();
    }
}

form.addEventListener('submit', async event => {
    event.preventDefault();
    start.disabled = true;
    try {
        const result = await login.start({
            method: 2, // Auðkenni app; use 0 for phone/SIM.
            phoneNumberOrSsn: input.value,
            language: 'en'
        });
        input.value = '';

        if (result.state !== 'Started') {
            status.textContent = 'Could not start sign-in. Please try again later.';
            return;
        }

        status.textContent = `Confirm code ${result.challenge} in your Auðkenni app.`;
        updateButtons();
        await checkStatus();
    } catch (error) {
        status.textContent = error.name === 'AbortError'
            ? 'Sign-in cancelled.'
            : 'Could not confirm sign-in. Cancel any active attempt before retrying.';
    } finally {
        input.value = '';
        updateButtons();
    }
});

checkAgain.addEventListener('click', async () => {
    try { await checkStatus(); }
    catch { status.textContent = 'Could not check sign-in. Please check again or cancel.'; }
});

cancel.addEventListener('click', async () => {
    cancel.disabled = true;
    try {
        const result = await login.cancel();
        status.textContent = result.state === 'Busy'
            ? 'Sign-in is already completing. Check its status again.'
            : 'Sign-in cancelled.';
    } catch {
        status.textContent = 'Cancellation could not be confirmed. Please try again.';
    } finally {
        updateButtons();
    }
});
</script>
```

For phone/SIM authentication, send `method: 0` with a phone number in
`phoneNumberOrSsn`, and ask the user to confirm the challenge on their phone.
Render challenges and messages with `textContent`, not `innerHTML`.

`login.wait()` polls sequentially while the result is `Pending`. After `Busy` or
`Unavailable`, it stops and retains `login.handle`. You can provide a **Check again**
button that calls `login.wait()` once the previous call has settled, or let the
user cancel. Do not call `start()` while a handle is active. The helper keeps its
handle in memory; page reload does not automatically resume an attempt.

For a complete page with both methods, see the
[HTML sample](samples/CustomLogin.Web/wwwroot/index.html).

### Browser result states

| State | What your UI should do |
| --- | --- |
| `Started` | Show the challenge and begin checking status. This is not a successful login. |
| `Pending` | Continue waiting for user approval. |
| `Completed` | The completion handler has run. Navigate to `redirect`. |
| `Failed` | Show a friendly failure and allow a new attempt. |
| `Cancelled` | Return to your login form. |
| `Expired` | Offer a fresh login. Attempts last two minutes. |
| `Busy` | Another operation or completion is in progress. Check again explicitly. |
| `Unavailable` | Stop automatic polling. Retain an existing handle for checking again or cancellation. |

Cancellation stops your website's attempt; it does not dismiss a prompt already
sent to the user's device. Once completion has been claimed, cancellation cannot
revoke it. If a start request times out or its response is lost, its outcome may
be unknown: do not automatically send another authentication request.

## Using the core client directly

Inject `IVettaClient` into your backend service. Start an authentication request
using the identifier submitted by the user:

```csharp
using Vetta.Connector;

public sealed class IdentityVerification(IVettaClient client)
{
    public Task<AuthenticationResult> StartAppAsync(
        string kennitala, CancellationToken cancellationToken) =>
        client.StartAsync(new StartRequest(
            AuthenticationMethod.App, kennitala, Language: "is"),
            cancellationToken);

    public Task<AuthenticationResult> StartPhoneAsync(
        string phoneNumber, CancellationToken cancellationToken) =>
        client.StartAsync(new StartRequest(
            AuthenticationMethod.Sim, phoneNumber, Language: "en"),
            cancellationToken);

    public Task<AuthenticationResult> CheckAsync(
        Guid attemptId, CancellationToken cancellationToken) =>
        client.WaitForStatusAsync(attemptId, cancellationToken);
}
```

On `AuthenticationState.Started`, save the returned `Identifier` in backend state
bound to the initiating browser and display `Challenge`. Retrieve that saved ID
when checking status; never accept an arbitrary attempt ID from another browser.

`WaitForStatusAsync` makes **one long-poll request**. It may return `Pending`;
call it again sequentially within your attempt's deadline. `GetStatusAsync` makes
one immediate status request instead. Only `Succeeded` with a non-null `Identity`
is proof of successful authentication:

```csharp
var result = await client.WaitForStatusAsync(savedAttemptId, cancellationToken);

if (result.State == AuthenticationState.Succeeded && result.Identity is { } identity)
{
    // Apply your account policy and create your website session here.
    // identity.Ssn is the verified kennitala; identity.Name is the verified name.
}
```

The core client does not create website sessions, store browser state, enforce
antiforgery, or coordinate concurrent completion. Your application must handle
those responsibilities if it does not use the ASP.NET Core helper.

`AuthenticateSimpleAsync(method, identifier, message, cancellationToken)` is also
available for a single-request authentication flow. Prefer the start/status flow
when your page needs to display a challenge and remain responsive while the user
approves the request.

### Errors and timeouts

Core results expose a controlled `AuthenticationError` value:

| Error | Suggested handling |
| --- | --- |
| `InvalidRequest` | Check input and request configuration. |
| `Unauthorized` | Check the configured application credentials. Do not ask the user to change their identity details. |
| `RateLimited` | Stop automatic requests and ask the user to wait. |
| `AuthenticationFailed` | Show a friendly unsuccessful-authentication message. |
| `TransportFailure`, `ServerUnavailable` | Treat the result as unavailable; preserve an existing attempt ID. |
| `InvalidResponse` | Treat the response as unusable and record a diagnostic event. |

Invalid local arguments can throw `ArgumentException`. Caller cancellation throws
`OperationCanceledException`; a request timeout throws `TimeoutException` and may
leave an unknown outcome. Handle these separately from an explicit authentication
failure. The ASP.NET Core browser helper exposes the simpler states listed above,
not the core error enum or verified personal data.

Record the operation, outcome, elapsed time, and an internal correlation ID for
troubleshooting. Do not log API keys, submitted identifiers, or whole result
objects: successful core results contain personal data.

## Production setup

When using the ASP.NET Core helper, register a singleton `IVettaAttemptStore`
**before** `AddVettaAuthenticationFlow`. The included development store is rejected
outside the `Development` environment. No production store adapter is bundled.

Your implementation must enforce browser ownership and expiry on every operation,
make `TryAdd`, `Bind`, and `Transition` atomic across instances, and reject stale
state transitions. Read the [store interface](src/Vetta.Connector.AspNetCore/AttemptStore.cs)
for its exact contract. A plain cache get/set sequence is not an atomic transition.

Also configure:

- Shared, persistent ASP.NET Core Data Protection keys across website instances
  for antiforgery and application cookies.
- Request limits and authentication quotas appropriate to your application. The
  development store's five-attempt-per-browser limit is not sufficient abuse control.
- Correct HTTPS/proxy configuration and your normal cookie/session policy.
- Account lookup, authorization, and idempotent completion in your handler.

## Run the sample

From the connector directory, configure the sample with development secrets:

```sh
dotnet user-secrets init --project samples/CustomLogin.Web
dotnet user-secrets set "Vetta:BaseUrl" "https://your-vetta-api.example/" --project samples/CustomLogin.Web
dotnet user-secrets set "Vetta:ApplicationId" "your-application-id" --project samples/CustomLogin.Web
dotnet user-secrets set "Vetta:ApiKey" "your-api-key" --project samples/CustomLogin.Web
dotnet run --project samples/CustomLogin.Web --launch-profile https
```

Open `https://localhost:7243/`. The sample performs real authentication with your
configured Vetta service and requires valid credentials. After approval, it creates
a demonstration cookie and opens `/verified`.

## License and releases

[MIT](LICENSE). Maintainers can find build and publishing instructions in the
[release guide](docs/releasing.md).
