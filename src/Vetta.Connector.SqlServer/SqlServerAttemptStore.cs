using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Vetta.Connector.AspNetCore;

namespace Vetta.Connector.SqlServer;

public static class SqlServerRegistration
{
    public static IServiceCollection AddVettaSqlServerAttemptStore(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _ = new SqlConnectionStringBuilder(connectionString);
        services.TryAddSingleton(TimeProvider.System);
        services.Replace(ServiceDescriptor.Singleton<SqlServerAttemptStore>(sp => new SqlServerAttemptStore(connectionString, sp.GetRequiredService<TimeProvider>())));
        services.Replace(ServiceDescriptor.Singleton<IVettaAttemptStore>(sp => sp.GetRequiredService<SqlServerAttemptStore>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SqlServerSchemaInitializer>());
        return services;
    }
}

internal sealed class SqlServerSchemaInitializer(SqlServerAttemptStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.InitializeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Shared SQL Server storage with atomic ownership and phase checks across application instances.</summary>
public sealed class SqlServerAttemptStore : IVettaAttemptStore
{
    private readonly string connectionString;
    private readonly TimeProvider time;
    private const int OperationSeconds = 330;
    private readonly SemaphoreSlim initializationGate = new(1, 1);
    private volatile bool initialized;
    public SqlServerAttemptStore(string connectionString, TimeProvider time)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _ = new SqlConnectionStringBuilder(connectionString);
        this.connectionString = connectionString; this.time = time;
    }
    /// <summary>Creates the attempt table and indexes if missing. Safe across concurrent application instances.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (initialized) return;
        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized) return;
            await using var db = new SqlConnection(connectionString);
            await db.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var stream = typeof(SqlServerAttemptStore).Assembly.GetManifestResourceStream(
                "Vetta.Connector.SqlServer.Migrations.001_CreateAttempts.sql")
                ?? throw new InvalidOperationException("The SQL Server attempt-storage migration is missing.");
            using var reader = new StreamReader(stream);
            using var command = db.CreateCommand();
            command.CommandText = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            command.CommandTimeout = 30;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally { initializationGate.Release(); }
    }

    private SqlConnection Open()
    {
        // Also support callers constructing the store directly without an ASP.NET Core host.
        InitializeAsync().GetAwaiter().GetResult();
        var connection = new SqlConnection(connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }
    private long Now => time.GetUtcNow().UtcTicks;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static SqlCommand Command(SqlConnection db, SqlTransaction? tx, string sql, params (string, object?)[] values)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql; command.CommandTimeout = 15;
        foreach (var (name, value) in values)
        {
            var type = name switch
            {
                "@now" or "@e" or "@lease" => SqlDbType.BigInt,
                "@to" or "@from" or "@ready" or "@starting" or "@failed" => SqlDbType.Int,
                "@u" => SqlDbType.UniqueIdentifier,
                "@h" or "@b" => SqlDbType.VarChar,
                _ => SqlDbType.NVarChar
            };
            var parameter = command.Parameters.Add(name, type);
            if (name is "@h" or "@b") parameter.Size = 64;
            if (name == "@k") parameter.Size = 32;
            if (name is "@o" or "@r") parameter.Size = 2048;
            parameter.Value = value ?? DBNull.Value;
        }
        return command;
    }
    public bool TryAdd(LoginAttempt attempt)
    {
        if (attempt.Handle.Length > 64 || attempt.Kind.Length > 32 || attempt.Origin?.Length > 2048 || attempt.Expires.UtcTicks <= Now) return false;
        using var db = Open(); using var tx = db.BeginTransaction();
        // Serialize capacity checks and insert across hosts; no in-process lock or cache race.
        using (var gate = Command(db, tx, """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource=N'VettaConnectorAttempts.Add',
                @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
            IF @result < 0 THROW 51000, 'Authentication attempt store is busy.', 1;
            """)) gate.ExecuteNonQuery();
        using (var purge = Command(db, tx, "DELETE FROM dbo.VettaConnectorAttempts WHERE Expires <= @now", ("@now", Now))) purge.ExecuteNonQuery();
        using var command = Command(db, tx, """
            INSERT INTO dbo.VettaConnectorAttempts (Handle,BrowserHash,UpstreamId,Expires,Kind,Origin,Phase,Redirect,Lease)
            SELECT @h,@b,@u,@e,@k,@o,0,NULL,@lease
            WHERE NOT EXISTS (SELECT 1 FROM dbo.VettaConnectorAttempts WHERE Handle=@h)
            AND (SELECT COUNT_BIG(*) FROM dbo.VettaConnectorAttempts) < 10000
            AND (SELECT COUNT_BIG(*) FROM dbo.VettaConnectorAttempts WHERE BrowserHash=@b) < 5;
            """, ("@h", attempt.Handle), ("@b", Hash(attempt.Browser)), ("@u", attempt.UpstreamId), ("@e", attempt.Expires.UtcTicks),
            ("@k", attempt.Kind), ("@o", attempt.Origin), ("@lease", time.GetUtcNow().AddSeconds(OperationSeconds).UtcTicks));
        var added = command.ExecuteNonQuery() == 1; tx.Commit(); return added;
    }
    public bool Bind(string handle, string browser, Guid upstreamId)
    {
        if (handle.Length > 64 || upstreamId == Guid.Empty) return false;
        using var db = Open();
        using var command = Command(db, null, "UPDATE dbo.VettaConnectorAttempts SET UpstreamId=@u, Phase=@ready, Lease=0 WHERE Handle=@h AND BrowserHash=@b AND Phase=@starting AND Expires>@now AND Lease>@now",
            ("@u", upstreamId), ("@ready", (int)AttemptPhase.Ready), ("@starting", (int)AttemptPhase.Starting), ("@h", handle), ("@b", Hash(browser)), ("@now", Now));
        return command.ExecuteNonQuery() == 1;
    }
    public AttemptSnapshot? Read(string handle, string browser)
    {
        if (handle.Length > 64) return null;
        using var db = Open(); using var tx = db.BeginTransaction();
        using (var expire = Command(db, tx, "UPDATE dbo.VettaConnectorAttempts SET Phase=@failed WHERE Handle=@h AND BrowserHash=@b AND Phase IN (0,2,3) AND Lease<=@now",
            ("@failed", (int)AttemptPhase.Failed), ("@h", handle), ("@b", Hash(browser)), ("@now", Now))) expire.ExecuteNonQuery();
        AttemptSnapshot? result = null;
        using (var command = Command(db, tx, "SELECT UpstreamId,Expires,Kind,Origin,Phase,Redirect FROM dbo.VettaConnectorAttempts WHERE Handle=@h AND BrowserHash=@b AND Expires>@now", ("@h", handle), ("@b", Hash(browser)), ("@now", Now)))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) result = new(new(handle, browser, reader.GetGuid(0), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)), (AttemptPhase)reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5));
        tx.Commit(); return result;
    }
    public bool Transition(string handle, string browser, AttemptPhase from, AttemptPhase to, string? redirect = null)
    {
        if (handle.Length > 64 || redirect?.Length > 2048) return false;
        using var db = Open();
        using var command = Command(db, null, """
            UPDATE dbo.VettaConnectorAttempts SET Phase=@to, Redirect=@r, Lease=@lease
            WHERE Handle=@h AND BrowserHash=@b AND Phase=@from AND Expires>@now
            AND (Phase NOT IN (0,2,3) OR Lease>@now)
            """, ("@to", (int)to), ("@r", redirect), ("@lease", to is AttemptPhase.Starting or AttemptPhase.Polling or AttemptPhase.Completing ? time.GetUtcNow().AddSeconds(OperationSeconds).UtcTicks : 0),
            ("@h", handle), ("@b", Hash(browser)), ("@from", (int)from), ("@now", Now));
        return command.ExecuteNonQuery() == 1;
    }
    public int PurgeExpired()
    {
        using var db = Open(); using var command = Command(db, null, "DELETE FROM dbo.VettaConnectorAttempts WHERE Expires<=@now", ("@now", Now)); return command.ExecuteNonQuery();
    }
}
