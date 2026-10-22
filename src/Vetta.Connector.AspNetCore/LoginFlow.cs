using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace Vetta.Connector.AspNetCore;

public interface IVettaLoginCompletion
{
    // Use CompletionId for idempotency. Sign-in/account policy belongs to the host.
    Task<string> CompleteAsync(HttpContext context, VerifiedIdentity identity, string completionId, CancellationToken cancellationToken);
}
public sealed record LoginReply(string State, string? Handle = null, string? Challenge = null, string? Redirect = null);

public sealed class VettaLoginFlow(IVettaClient client, IVettaAttemptStore store, IVettaLoginCompletion completion, TimeProvider time)
{
    public async Task<LoginReply> StartAsync(string browser, StartRequest request, CancellationToken ct)
    {
        // Reserve capacity before sending a provider-start request.
        var handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var attempt = new LoginAttempt(handle, browser, Guid.Empty, time.GetUtcNow().AddMinutes(2));
        if (!store.TryAdd(attempt)) return new("Unavailable");
        try
        {
            var result = await client.StartAsync(request, ct);
            if (result.State != AuthenticationState.Started || result.Identifier is null ||
                !store.Bind(handle, browser, result.Identifier.Value))
            {
                store.Transition(handle, browser, AttemptPhase.Starting, AttemptPhase.Failed);
                return new("Unavailable");
            }
            return new("Started", handle, result.Challenge);
        }
        catch
        {
            store.Transition(handle, browser, AttemptPhase.Starting, AttemptPhase.Failed);
            throw;
        }
    }

    public async Task<LoginReply> StatusAsync(HttpContext context, string browser, string handle, CancellationToken ct)
    {
        var entry = store.Read(handle, browser);
        if (entry is null) return new("Expired");
        if (entry.Phase == AttemptPhase.Completed) return new("Completed", Redirect: entry.Redirect);
        if (entry.Phase is AttemptPhase.Cancelled or AttemptPhase.Failed) return new(entry.Phase.ToString());
        if (!store.Transition(handle, browser, AttemptPhase.Ready, AttemptPhase.Polling)) return new("Busy");
        try
        {
            var result = await client.WaitForStatusAsync(entry.Attempt.UpstreamId, ct);
            ct.ThrowIfCancellationRequested();
            if (result.State == AuthenticationState.Pending)
            {
                return store.Transition(handle, browser, AttemptPhase.Polling, AttemptPhase.Ready) ? new("Pending") : new("Cancelled");
            }
            if (result.State == AuthenticationState.Unavailable)
            {
                store.Transition(handle, browser, AttemptPhase.Polling, AttemptPhase.Ready);
                return new("Unavailable");
            }
            if (result.State != AuthenticationState.Succeeded || result.Identity is null)
            {
                store.Transition(handle, browser, AttemptPhase.Polling, AttemptPhase.Failed);
                return new("Failed");
            }
            if (!store.Transition(handle, browser, AttemptPhase.Polling, AttemptPhase.Completing)) return new("Cancelled");
            // Cancellation cannot revoke completion once claimed. Host side effects must be idempotent.
            try
            {
                var redirect = await completion.CompleteAsync(context, result.Identity, handle, ct);
                if (!IsLocal(redirect)) throw new InvalidOperationException("Completion must return a local URL.");
                if (!store.Transition(handle, browser, AttemptPhase.Completing, AttemptPhase.Completed, redirect)) return new("Expired");
                return new("Completed", Redirect: redirect);
            }
            catch
            {
                store.Transition(handle, browser, AttemptPhase.Completing, AttemptPhase.Failed);
                throw;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { return new("Unavailable"); }
        finally { store.Transition(handle, browser, AttemptPhase.Polling, AttemptPhase.Ready); }
    }

    public LoginReply Cancel(string browser, string handle)
    {
        var entry = store.Read(handle, browser);
        if (entry is null) return new("Expired");
        if (entry.Phase == AttemptPhase.Cancelled) return new("Cancelled");
        if (store.Transition(handle, browser, AttemptPhase.Ready, AttemptPhase.Cancelled) ||
            store.Transition(handle, browser, AttemptPhase.Polling, AttemptPhase.Cancelled)) return new("Cancelled");
        return new("Busy");
    }
    private static bool IsLocal(string url) => !string.IsNullOrEmpty(url) && url[0] == '/' &&
        (url.Length == 1 || url[1] is not ('/' or '\\')) && !url.Any(char.IsControl) && !url.Contains('\\');
}
