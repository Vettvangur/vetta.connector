# SQL Server authentication attempt storage

`Vetta.Connector.SqlServer` (upcoming 0.3.0) provides persistent `IVettaAttemptStore`
storage for SQL Server and Azure SQL. All instances of a customer website use the
same connection string and table. It supports SIM, app, device-link, and
customer-domain passkey attempts.

## Automatic schema setup

Registering the store automatically applies the embedded
[001_CreateAttempts.sql](../src/Vetta.Connector.SqlServer/Migrations/001_CreateAttempts.sql)
at application startup in **your website's existing database**. No manual migration
step is required. It creates only `dbo.VettaConnectorAttempts` and its indexes if
missing, preserving existing attempts. A database application lock makes setup safe
when multiple website instances start together. Startup fails if initialization fails;
fix database connectivity or permissions and restart.

The database itself must already exist. Customers never need direct access to Vetta's
central database. Use one logical store per website/application; all replicas share it.
Directly constructed stores initialize automatically on their first operation.

## Register the store

```csharp
using Vetta.Connector.SqlServer;

builder.Services.AddVettaSqlServerAttemptStore(
    builder.Configuration.GetConnectionString("VettaAttempts")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:VettaAttempts."));
```

For example, configure `ConnectionStrings:VettaAttempts` through your secret store:

```text
Server=your-sql-host;Database=your-website;Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False
```

Choose the SQL authentication method supported by your deployment. Keep credentials
out of source control. The runtime principal needs SELECT, INSERT, UPDATE and DELETE
on `dbo.VettaConnectorAttempts`, and permission to use `sys.sp_getapplock` under the
same database principal across instances. For automatic table creation, it also needs
CREATE TABLE on the database and ALTER on the dbo schema. Database creation rights
are not required. Keep this connection limited to your website database.

## Concurrency and recovery

The table stores browser-binding hashes, upstream identifiers, expiry, phase, and a
local completion redirect. It does not store verified identities or API keys.
Parameterized updates check browser ownership, expiry, and expected phase atomically.
A transaction-owned application lock serializes new reservations across hosts, so
capacity checks and insertion cannot race (10,000 live attempts total, five per browser).

An interrupted operation is never automatically replayed. A 330-second lease covers
the maximum five-minute connector HTTP timeout. Once the lease or attempt expires,
the user starts again. Completion handlers must remain idempotent: your account
writes, the attempt transition, and delivery of a browser cookie are separate operations.
Keep host clocks synchronized.

New starts purge expired rows. `SqlServerAttemptStore.PurgeExpired()` can also run in
scheduled maintenance for idle deployments. If SQL is unavailable, the connector
cannot establish ownership and must not complete a login from cached browser data.
Handle infrastructure failures with a friendly retry message and safe server logging.

Persist and share ASP.NET Core Data Protection keys across website instances, with
the same application name and cookie settings. Database persistence does not itself
preserve antiforgery or login-cookie validation. Apply rate limits to authentication
starts; the per-browser capacity limit alone is not sufficient abuse protection.

## Tests

CI starts an isolated SQL Server container and runs migration, persistence, expiry,
ownership and concurrent reservation/completion tests. To run these locally, set
`VETTA_TEST_SQLSERVER` to a **disposable test SQL Server** whose principal may create
and drop databases. Tests create randomly named `VettaConnectorTest_*` databases and
delete those databases afterwards. Without that variable, SQL integration tests are
reported as skipped. Do not point it at a production service.
