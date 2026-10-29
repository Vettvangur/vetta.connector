# Persistent authentication attempts

`Vetta.Connector.Sqlite` (upcoming 0.3.0) provides a supported persistent
`IVettaAttemptStore` for **one host**, including multiple application processes
sharing one database file. It supports SIM, app, device-link, and customer-domain
passkey attempts.

```csharp
using Vetta.Connector.Sqlite;
builder.Services.AddVettaSqliteAttemptStore("/var/lib/my-site/vetta-attempts.db");
```

Create the parent directory first. Use a dedicated database on persistent **local**
storage. The application creates its `VettaAttempts` table and indexes automatically.
Restrict file and directory permissions to your application identity; the database
contains short-lived authentication handles and must not be publicly served.

The store persists browser-binding hashes, upstream identifiers, expiration, phase,
and a local completion redirect. It does not store verified identities or API keys.
Writes and compare-and-set transitions are atomic across processes. Capacity checks
are performed in the same transaction as insertion (10,000 total live attempts,
five per browser). Expired rows are purged on new starts; `PurgeExpired()` is available
for periodic maintenance on idle deployments.

An operation that was interrupted by a process crash is never replayed automatically.
A lease of 330 seconds covers the connector's maximum five-minute HTTP timeout.
When the lease or attempt expires, the user must start again. This favors preventing
duplicate sign-in side effects over transparently resuming an uncertain completion.
Your completion handler must still be idempotent: the database cannot atomically
commit your own account writes and the browser's cookie delivery.

**Deployment boundary:** do not share this file over NFS, SMB, or between container
hosts. Do not use ephemeral container storage. For multiple hosts, implement
`IVettaAttemptStore` against a shared transactional database with atomic ownership,
capacity checks, expiry, and phase transitions. Sticky sessions alone do not make
an in-memory store persistent. The in-memory store remains Development-only.

Also persist ASP.NET Core Data Protection keys across restarts and share them across
processes serving the same website. Use the same application name and cookie settings.
Database persistence alone cannot preserve antiforgery or login-cookie validation.
Apply your site's rate limits to paid authentication starts; per-browser capacity is
not sufficient abuse protection.
