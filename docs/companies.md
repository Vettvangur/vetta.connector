# Company sign-in (fyrirtækjaumboð) on your own sign-in page

When your organisation has Vetta's company sign-in add-on, people who sign in on your own page can choose a company they hold prókúra for, or were given access to, and sign in on its behalf. On Vetta's hosted login page this happens automatically once company sign-in is switched on for your application; your site does not need to ask for anything (see [OpenID Connect](openid-connect.md)). With the direct API on your own page, use `IVettaCompanyClient`. It is registered by `AddVettaConnector` and runs on your server only, with your application's API key.

## Flow

```csharp
// 1. The person signed in with the direct API (StartAsync, then WaitForStatusAsync) and you have a Succeeded result.
var opened = await companies.OpenSessionAsync(result.Identifier!.Value, ct);   // within two minutes of the sign-in
if (!opened.Succeeded) { /* addon_disabled, sign_in_not_found, …: sign the person in as themselves */ }
var session = opened.Value!.Token;   // keep with the person's own session on your server, never in the browser
var choices = opened.Value.Choices;  // Companies, Invitations, RegistryUnavailable

// 2. Show "sign in as": the person, each company (NationalId, Name, Role, ExpiresUtc) and each invitation.
// 3a. Signing in as the person needs no call.
// 3b. Signing in as a company:
var chosen = await companies.ChooseAsync(session, "550169-2829", ct);
if (chosen.Succeeded) SignIn(person, company: chosen.Value!);   // Id (kennitala), Name, Role
```

`Role` is `procuration_holder`, `manager` or `member` (`VettaCompanyRoles`). Vetta checks the company register every time a company is chosen, so a struck-off company or ended access is refused (`company_forbidden`) even if it was listed a moment ago.

## Invitations

`AnswerInvitationAsync(session, invitation.Id, accept)` accepts or declines access someone offered the person, and returns the updated choices.

## Managing access (prókúruhafar and managers)

Companies with `CanManage` can be managed by the person:

| Call | Does |
| --- | --- |
| `GetAccessAsync(session, company)` | Everyone with access in your organisation. Prókúruhafar come from the register, have no `AccessId` and can never be removed. Kennitölur are masked. |
| `InviteAsync(session, company, kennitala, role, expiresUtc, email)` | Gives access as `manager` or `member`, until removed or until `expiresUtc`. With an e-mail address Vetta tells the person; the address is not stored. They accept at their next sign-in. |
| `ChangeAccessAsync(session, company, accessId, revision, role, expiresUtc)` | Changes role and end (`null` = until removed). |
| `RemoveAccessAsync(session, accessId, revision)` | Removes access; the person's own access is leaving. |

Access is per Vetta organisation: access given on your site works only in your organisation's applications.

## Results and errors

Every call returns `VettaCompanyResult<T>`. When `Succeeded` is false, `Error` is one of:

| Error | Meaning |
| --- | --- |
| `addon_disabled` | The organisation does not have company sign-in switched on. |
| `sign_in_not_found` | Not a succeeded direct sign-in of this application from the last two minutes. |
| `session_exists` | That sign-in already opened a company session. |
| `session_expired` | The company session ended (after an hour). Sign the person in again. |
| `company_forbidden` | The person may not do that for this company, or it is no longer active. |
| `company_invalid` | A value was refused, for example a kennitala, date or e-mail address. |
| `company_conflict` | Already has access, holds prókúra, a limit, or a stale `revision`. |
| `company_not_found` | The access no longer exists. |
| `company_unavailable` | The company register could not be reached. Try again. |
| `unauthorized`, `server_unavailable`, `rate_limited`, `transport_failure`, `invalid_response` | As for the other clients. |

`Message` explains a refusal in English for logs; show your own text to people.
