using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vetta.Connector.AspNetCore;

namespace Vetta.Connector.Sqlite;

public static class SqliteRegistration
{
    /// <summary>Use a dedicated database file on persistent local storage. Not suitable for shared/network filesystems.</summary>
    public static IServiceCollection AddVettaSqliteAttemptStore(this IServiceCollection services, string absolutePath)
    {
        if (!Path.IsPathFullyQualified(absolutePath)) throw new ArgumentException("An absolute database file path is required.");
        services.TryAddSingleton(TimeProvider.System);
        services.Replace(ServiceDescriptor.Singleton<IVettaAttemptStore>(sp => new SqliteAttemptStore(absolutePath, sp.GetRequiredService<TimeProvider>())));
        return services;
    }
}

/// <summary>Atomic across processes on one host. Interrupted operations fail closed after their lease expires.</summary>
public sealed class SqliteAttemptStore : IVettaAttemptStore
{
    private readonly string connectionString;
    private readonly TimeProvider time;
    private const int OperationSeconds = 330; // Longer than the maximum connector HTTP timeout (five minutes).
    public SqliteAttemptStore(string absolutePath, TimeProvider time)
    {
        if (!Path.IsPathFullyQualified(absolutePath)) throw new ArgumentException("An absolute database file path is required.");
        this.time = time;
        connectionString = new SqliteConnectionStringBuilder { DataSource = absolutePath, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS VettaAttempts (
              Handle TEXT PRIMARY KEY, BrowserHash TEXT NOT NULL, UpstreamId TEXT NOT NULL,
              Expires INTEGER NOT NULL, Kind TEXT NOT NULL, Origin TEXT NULL,
              Phase INTEGER NOT NULL, Redirect TEXT NULL, Lease INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_VettaAttempts_Browser ON VettaAttempts(BrowserHash);
            CREATE INDEX IF NOT EXISTS IX_VettaAttempts_Expires ON VettaAttempts(Expires);
            """;
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    private long Now => time.GetUtcNow().UtcTicks;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object?)[] values)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public bool TryAdd(LoginAttempt attempt)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using (var purge = Command(db, tx, "DELETE FROM VettaAttempts WHERE Expires <= $now", ("$now", Now))) purge.ExecuteNonQuery();
        using var command = Command(db, tx, """
            INSERT OR IGNORE INTO VettaAttempts
            SELECT $h,$b,$u,$e,$k,$o,$p,NULL,$l
            WHERE (SELECT COUNT(*) FROM VettaAttempts) < 10000
            AND (SELECT COUNT(*) FROM VettaAttempts WHERE BrowserHash=$b) < 5
            """,
            ("$h", attempt.Handle), ("$b", Hash(attempt.Browser)), ("$u", attempt.UpstreamId.ToString("D")), ("$e", attempt.Expires.UtcTicks),
            ("$k", attempt.Kind), ("$o", attempt.Origin), ("$p", (int)AttemptPhase.Starting), ("$l", time.GetUtcNow().AddSeconds(OperationSeconds).UtcTicks));
        var added = command.ExecuteNonQuery() == 1; tx.Commit(); return added;
    }
    public bool Bind(string handle, string browser, Guid upstreamId)
    {
        using var db = Open();
        using var command = Command(db, null, "UPDATE VettaAttempts SET UpstreamId=$u, Phase=$ready, Lease=0 WHERE Handle=$h AND BrowserHash=$b AND Phase=$starting AND Expires>$now AND Lease>$now",
            ("$u", upstreamId.ToString("D")), ("$ready", (int)AttemptPhase.Ready), ("$starting", (int)AttemptPhase.Starting), ("$h", handle), ("$b", Hash(browser)), ("$now", Now));
        return command.ExecuteNonQuery() == 1;
    }
    public AttemptSnapshot? Read(string handle, string browser)
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using (var expire = Command(db, tx, "UPDATE VettaAttempts SET Phase=$failed WHERE Handle=$h AND BrowserHash=$b AND Phase IN (0,2,3) AND Lease<=$now",
            ("$failed", (int)AttemptPhase.Failed), ("$h", handle), ("$b", Hash(browser)), ("$now", Now))) expire.ExecuteNonQuery();
        AttemptSnapshot? result = null;
        using (var command = Command(db, tx, "SELECT UpstreamId,Expires,Kind,Origin,Phase,Redirect FROM VettaAttempts WHERE Handle=$h AND BrowserHash=$b AND Expires>$now", ("$h", handle), ("$b", Hash(browser)), ("$now", Now)))
        using (var reader = command.ExecuteReader())
            if (reader.Read()) result = new(new(handle, browser, Guid.Parse(reader.GetString(0)), new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)), (AttemptPhase)reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5));
        tx.Commit(); return result;
    }
    public bool Transition(string handle, string browser, AttemptPhase from, AttemptPhase to, string? redirect = null)
    {
        using var db = Open();
        using var command = Command(db, null, """
            UPDATE VettaAttempts SET Phase=$to, Redirect=$r, Lease=$lease
            WHERE Handle=$h AND BrowserHash=$b AND Phase=$from AND Expires>$now
            AND (Phase NOT IN (0,2,3) OR Lease>$now)
            """, ("$to", (int)to), ("$r", redirect), ("$lease", to is AttemptPhase.Starting or AttemptPhase.Polling or AttemptPhase.Completing ? time.GetUtcNow().AddSeconds(OperationSeconds).UtcTicks : 0),
            ("$h", handle), ("$b", Hash(browser)), ("$from", (int)from), ("$now", Now));
        return command.ExecuteNonQuery() == 1;
    }
    /// <summary>Optional maintenance for idle deployments; new attempts also purge expired rows.</summary>
    public int PurgeExpired()
    {
        using var db = Open(); using var command = Command(db, null, "DELETE FROM VettaAttempts WHERE Expires<=$now", ("$now", Now)); return command.ExecuteNonQuery();
    }
}
