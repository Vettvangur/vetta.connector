using Vetta.Connector.AspNetCore;
using Vetta.Connector.Sqlite;
using Microsoft.Data.Sqlite;
namespace Vetta.Connector.Tests;

public sealed class SqliteTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), "vetta-test-" + Guid.NewGuid() + ".db");
    private readonly Clock clock = new();
    private SqliteAttemptStore Store() => new(file, clock);
    private LoginAttempt Attempt(string handle = "handle", string browser = "browser") => new(handle, browser, Guid.Empty, clock.GetUtcNow().AddMinutes(10));
    [Fact]
    public void Restart_preserves_binding_completion_and_excludes_other_browser()
    {
        var store = Store(); Assert.True(store.TryAdd(Attempt())); var id = Guid.NewGuid();
        Assert.True(store.Bind("handle", "browser", id));
        var reopened = Store(); Assert.Equal(id, reopened.Read("handle", "browser")!.Attempt.UpstreamId);
        Assert.Null(reopened.Read("handle", "other")); Assert.False(reopened.Transition("handle", "other", AttemptPhase.Ready, AttemptPhase.Polling));
        Assert.True(reopened.Transition("handle", "browser", AttemptPhase.Ready, AttemptPhase.Completed, "/account"));
        Assert.Equal("/account", Store().Read("handle", "browser")!.Redirect);
    }
    [Fact]
    public async Task Independent_instances_claim_completion_exactly_once()
    {
        var one = Store(); var two = Store(); one.TryAdd(Attempt()); one.Bind("handle", "browser", Guid.NewGuid());
        var results = await Task.WhenAll(Task.Run(() => one.Transition("handle", "browser", AttemptPhase.Ready, AttemptPhase.Completing)), Task.Run(() => two.Transition("handle", "browser", AttemptPhase.Ready, AttemptPhase.Completing)));
        Assert.Equal(1, results.Count(x => x));
    }
    [Fact]
    public void Abandoned_operation_fails_closed_and_expiry_purges_rows()
    {
        var store = Store(); store.TryAdd(Attempt()); store.Bind("handle", "browser", Guid.NewGuid());
        store.Transition("handle", "browser", AttemptPhase.Ready, AttemptPhase.Completing);
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(store.Transition("handle", "browser", AttemptPhase.Completing, AttemptPhase.Completed));
        Assert.Equal(AttemptPhase.Failed, Store().Read("handle", "browser")!.Phase);
        clock.Advance(TimeSpan.FromMinutes(5)); Assert.Null(store.Read("handle", "browser")); Assert.Equal(1, store.PurgeExpired());
    }
    [Fact]
    public void Capacity_and_duplicates_are_enforced_across_instances()
    {
        var store = Store(); Assert.True(store.TryAdd(Attempt())); Assert.False(Store().TryAdd(Attempt()));
        for (var i = 0; i < 4; i++) Assert.True(Store().TryAdd(Attempt(i.ToString())));
        Assert.False(Store().TryAdd(Attempt("overflow")));
        Assert.True(Store().TryAdd(Attempt("other", "second-browser")));
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); File.Delete(file); }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
