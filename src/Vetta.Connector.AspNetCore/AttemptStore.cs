namespace Vetta.Connector.AspNetCore;

public sealed record LoginAttempt(string Handle, string Browser, Guid UpstreamId, DateTimeOffset Expires);
public enum AttemptPhase { Starting, Ready, Polling, Completing, Completed, Cancelled, Failed }
public sealed record AttemptSnapshot(LoginAttempt Attempt, AttemptPhase Phase, string? Redirect = null);

// Implementations must make every transition atomic, including across instances.
public interface IVettaAttemptStore
{
    bool TryAdd(LoginAttempt attempt);
    bool Bind(string handle, string browser, Guid upstreamId);
    AttemptSnapshot? Read(string handle, string browser);
    bool Transition(string handle, string browser, AttemptPhase from, AttemptPhase to, string? redirect = null);
}

// Development only. This store is intentionally not a distributed-cache abstraction.
public sealed class DevelopmentAttemptStore(TimeProvider time) : IVettaAttemptStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, AttemptSnapshot> attempts = new();
    public bool TryAdd(LoginAttempt attempt)
    {
        lock (gate)
        {
            foreach (var key in attempts.Where(x => x.Value.Attempt.Expires <= time.GetUtcNow()).Select(x => x.Key).ToArray()) attempts.Remove(key);
            if (attempts.Count >= 10000 || attempts.Values.Count(x => x.Attempt.Browser == attempt.Browser) >= 5) return false;
            return attempts.TryAdd(attempt.Handle, new(attempt, AttemptPhase.Starting));
        }
    }
    public bool Bind(string handle, string browser, Guid upstreamId)
    {
        lock (gate)
        {
            var item = Get(handle, browser);
            if (item is null || item.Phase != AttemptPhase.Starting) return false;
            attempts[handle] = item with { Attempt = item.Attempt with { UpstreamId = upstreamId }, Phase = AttemptPhase.Ready };
            return true;
        }
    }
    public AttemptSnapshot? Read(string handle, string browser)
    {
        lock (gate) return Get(handle, browser);
    }
    private AttemptSnapshot? Get(string handle, string browser) =>
        attempts.TryGetValue(handle, out var item) && item.Attempt.Browser == browser && item.Attempt.Expires > time.GetUtcNow() ? item : null;
    public bool Transition(string handle, string browser, AttemptPhase from, AttemptPhase to, string? redirect = null)
    {
        lock (gate)
        {
            var item = Get(handle, browser);
            if (item is null || item.Phase != from) return false;
            attempts[handle] = item with { Phase = to, Redirect = redirect };
            return true;
        }
    }
}
