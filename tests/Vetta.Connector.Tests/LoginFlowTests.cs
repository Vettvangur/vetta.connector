using Microsoft.AspNetCore.Http;
using Vetta.Connector.AspNetCore;

namespace Vetta.Connector.Tests;

public sealed class LoginFlowTests
{
    [Fact]
    public async Task Different_browser_cannot_poll_or_complete_an_attempt()
    {
        var fixture = new Fixture();
        var start = await fixture.Start();
        Assert.Equal("Expired", (await fixture.Flow.StatusAsync(new DefaultHttpContext(), "attacker", start.Handle!, default)).State);
        Assert.Equal("Expired", fixture.Flow.Cancel("attacker", start.Handle!).State);
        Assert.Equal(0, fixture.Client.Polls);
        Assert.Equal(0, fixture.Completion.Calls);
    }
    [Fact]
    public async Task Replay_returns_completion_without_repeating_side_effects()
    {
        var fixture = new Fixture();
        var start = await fixture.Start();
        Assert.Equal("Completed", (await fixture.Status(start)).State);
        Assert.Equal("Completed", (await fixture.Status(start)).State);
        Assert.Equal(1, fixture.Client.Polls);
        Assert.Equal(1, fixture.Completion.Calls);
    }
    [Fact]
    public async Task Cancel_during_poll_blocks_late_success()
    {
        var fixture = new Fixture();
        var pending = new TaskCompletionSource<AuthenticationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Wait = () => pending.Task;
        var start = await fixture.Start();
        var status = fixture.Status(start);
        Assert.Equal("Busy", (await fixture.Status(start)).State);
        Assert.Equal("Cancelled", fixture.Flow.Cancel("browser", start.Handle!).State);
        pending.SetResult(Success());
        Assert.Equal("Cancelled", (await status).State);
        Assert.Equal(0, fixture.Completion.Calls);
        Assert.Equal(1, fixture.Client.Polls);
    }
    [Fact]
    public async Task Expiry_blocks_completion()
    {
        var fixture = new Fixture();
        var start = await fixture.Start();
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(3);
        Assert.Equal("Expired", (await fixture.Status(start)).State);
        Assert.Equal(0, fixture.Completion.Calls);
    }
    [Fact]
    public async Task Capacity_is_reserved_before_starting_provider()
    {
        var fixture = new Fixture();
        for (var i = 0; i < 5; i++) Assert.Equal("Started", (await fixture.Start()).State);
        Assert.Equal("Unavailable", (await fixture.Start()).State);
        Assert.Equal(5, fixture.Client.Starts);
    }
    [Fact]
    public async Task Completion_exception_is_not_retried()
    {
        var fixture = new Fixture();
        fixture.Completion.Fail = true;
        var start = await fixture.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Status(start));
        Assert.Equal("Failed", (await fixture.Status(start)).State);
        Assert.Equal(1, fixture.Completion.Calls);
    }
    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    public async Task External_redirects_are_rejected(string redirect)
    {
        var fixture = new Fixture();
        fixture.Completion.Redirect = redirect;
        var start = await fixture.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Status(start));
        Assert.Equal("Failed", (await fixture.Status(start)).State);
    }
    [Fact]
    public async Task Pending_and_timeout_release_poll_claim_without_auto_retry()
    {
        var fixture = new Fixture();
        var start = await fixture.Start();
        fixture.Client.Wait = () => Task.FromResult(new AuthenticationResult(AuthenticationState.Pending));
        Assert.Equal("Pending", (await fixture.Status(start)).State);
        fixture.Client.Wait = () => throw new TimeoutException();
        Assert.Equal("Unavailable", (await fixture.Status(start)).State);
        fixture.Client.Wait = () => Task.FromResult(Success());
        Assert.Equal("Completed", (await fixture.Status(start)).State);
        Assert.Equal(3, fixture.Client.Polls);
    }
    private static AuthenticationResult Success() => new(AuthenticationState.Succeeded, Identity: new("Fixture", "1234567890"));
    private sealed class Fixture
    {
        public Clock Clock { get; } = new();
        public Client Client { get; } = new();
        public Completion Completion { get; } = new();
        public VettaLoginFlow Flow { get; }
        public Fixture() => Flow = new(Client, new DevelopmentAttemptStore(Clock), Completion, Clock);
        public Task<LoginReply> Start() => Flow.StartAsync("browser", new(AuthenticationMethod.App, "1234567890"), default);
        public Task<LoginReply> Status(LoginReply start) => Flow.StatusAsync(new DefaultHttpContext(), "browser", start.Handle!, default);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Completion : IVettaLoginCompletion
    {
        public int Calls;
        public bool Fail;
        public string Redirect = "/account";
        public Task<string> CompleteAsync(HttpContext context, VerifiedIdentity identity, string id, CancellationToken ct)
        { Calls++; if (Fail) throw new InvalidOperationException(); return Task.FromResult(Redirect); }
    }
    private sealed class Client : IVettaClient
    {
        public int Starts, Polls;
        public Func<Task<AuthenticationResult>> Wait = () => Task.FromResult(Success());
        public Task<AuthenticationResult> StartAsync(StartRequest request, CancellationToken ct = default)
        { Starts++; return Task.FromResult(new AuthenticationResult(AuthenticationState.Started, Guid.NewGuid(), "1234")); }
        public Task<AuthenticationResult> WaitForStatusAsync(Guid id, CancellationToken ct = default) { Polls++; return Wait(); }
        public Task<AuthenticationResult> GetStatusAsync(Guid id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AuthenticationResult> AuthenticateSimpleAsync(AuthenticationMethod method, string input, string? message = null, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
