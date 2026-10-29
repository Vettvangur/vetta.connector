using Vetta.Connector.AspNetCore;
using Vetta.Connector.SqlServer;
using Microsoft.Data.SqlClient;
namespace Vetta.Connector.Tests;

public sealed class SqlServerTests : IDisposable
{
    private readonly string database = "VettaConnectorTest_" + Guid.NewGuid().ToString("N");
    private readonly string adminConnection;
    private readonly string connection;
    public SqlServerTests()
    {
        var builder = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("VETTA_TEST_SQLSERVER"));
        builder.InitialCatalog = "master"; builder.ConnectTimeout = 2;
        adminConnection = builder.ConnectionString;
        using var db = new SqlConnection(adminConnection);
        for (var attempt = 0; ; attempt++)
        {
            try { db.Open(); break; }
            catch (SqlException) when (attempt < 59) { Thread.Sleep(500); }
        }
        using (var create = db.CreateCommand()) { create.CommandText = $"CREATE DATABASE [{database}]"; create.ExecuteNonQuery(); }
        builder.InitialCatalog = database; connection = builder.ConnectionString;

    }
    private readonly Clock clock = new();
    private SqlServerAttemptStore Store() => new(connection, clock);
    private LoginAttempt Attempt(string handle = "handle", string browser = "browser") => new(handle, browser, Guid.Empty, clock.GetUtcNow().AddMinutes(10));
    [SqlServerFact]
    public void Restart_preserves_binding_completion_and_excludes_other_browser()
    {
        var store = Store(); Assert.True(store.TryAdd(Attempt())); var id = Guid.NewGuid();
        Assert.True(store.Bind("handle", "browser", id));
        var reopened = Store(); Assert.Equal(id, reopened.Read("handle", "browser")!.Attempt.UpstreamId);
        Assert.Null(reopened.Read("handle", "other")); Assert.False(reopened.Transition("handle", "other", AttemptPhase.Ready, AttemptPhase.Polling));
        Assert.True(reopened.Transition("handle", "browser", AttemptPhase.Ready, AttemptPhase.Completed, "/account"));
        Assert.Equal("/account", Store().Read("handle", "browser")!.Redirect);
    }
    [SqlServerFact]
    public async Task Independent_instances_claim_completion_exactly_once()
    {
        var one = Store(); var two = Store(); one.TryAdd(Attempt()); one.Bind("handle", "browser", Guid.NewGuid());
        var results = await Task.WhenAll(Task.Run(() => one.Transition("handle", "browser", AttemptPhase.Ready, AttemptPhase.Completing)), Task.Run(() => two.Transition("handle", "browser", AttemptPhase.Ready, AttemptPhase.Completing)));
        Assert.Equal(1, results.Count(x => x));
    }
    [SqlServerFact]
    public void Abandoned_operation_fails_closed_and_expiry_purges_rows()
    {
        var store = Store(); store.TryAdd(Attempt()); store.Bind("handle", "browser", Guid.NewGuid());
        store.Transition("handle", "browser", AttemptPhase.Ready, AttemptPhase.Completing);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(store.Transition("handle", "browser", AttemptPhase.Completing, AttemptPhase.Completed));
        Assert.Equal(AttemptPhase.Failed, Store().Read("handle", "browser")!.Phase);
        clock.Advance(TimeSpan.FromMinutes(5)); Assert.Null(store.Read("handle", "browser")); Assert.Equal(1, store.PurgeExpired());
    }
    [SqlServerFact]
    public void Capacity_and_duplicates_are_enforced_across_instances()
    {
        var store = Store(); Assert.True(store.TryAdd(Attempt())); Assert.False(Store().TryAdd(Attempt()));
        for (var i = 0; i < 4; i++) Assert.True(Store().TryAdd(Attempt(i.ToString())));
        Assert.False(Store().TryAdd(Attempt("overflow")));
        Assert.True(Store().TryAdd(Attempt("other", "second-browser")));
    }
    [SqlServerFact]
    public async Task Concurrent_reservations_cannot_exceed_browser_capacity()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => Store().TryAdd(Attempt(i.ToString())))));
        Assert.Equal(5, results.Count(x => x));
    }
    [SqlServerFact]
    public async Task Initialization_is_automatic_concurrent_and_preserves_existing_rows()
    {
        var stores = Enumerable.Range(0, 8).Select(_ => Store()).ToArray();
        await Task.WhenAll(stores.Select(store => store.InitializeAsync()));
        Assert.True(stores[0].TryAdd(Attempt()));
        await Store().InitializeAsync();
        Assert.NotNull(Store().Read("handle", "browser"));
    }
    [SqlServerFact]
    public async Task Registration_initializes_schema_when_host_starts()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        SqlServerRegistration.AddVettaSqlServerAttemptStore(builder.Services, connection);
        using var host = builder.Build();
        await host.StartAsync();
        using var db = new SqlConnection(connection); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dbo.VettaConnectorAttempts";
        Assert.Equal(0, (int)command.ExecuteScalar()!);
        await host.StopAsync();
    }
    public void Dispose()
    {
        SqlConnection.ClearAllPools();
        using var db = new SqlConnection(adminConnection); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]";
        command.ExecuteNonQuery();
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VETTA_TEST_SQLSERVER")))
            Skip = "Set VETTA_TEST_SQLSERVER to a disposable SQL Server with database creation permission.";
    }
}
