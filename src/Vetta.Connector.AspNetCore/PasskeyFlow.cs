using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
namespace Vetta.Connector.AspNetCore;

public sealed record PasskeyLoginReply(string State, string? Handle = null, string? OptionsJson = null, string? Redirect = null) { public string? ErrorCode { get; init; } }
public sealed class VettaPasskeyFlow(IVettaPasskeyClient client, IVettaAttemptStore store, IVettaLoginCompletion completion, TimeProvider clock)
{
    public async Task<PasskeyResponse> ManageAsync(string browser, string completedHandle, string? credentialId, CancellationToken ct)
    {
        var verified = store.Read(completedHandle, browser);
        if (verified is not { Phase: AttemptPhase.Completed, Attempt.Kind: "audkenni" }) return new("Unavailable");
        var policy = await client.GetPolicyAsync(ct);
        if (policy is not { Scope: "CustomerDomain", Origin: not null }) return new("Unavailable");
        return credentialId is null ? await client.ListAsync(policy.Origin, browser, verified.Attempt.UpstreamId, ct)
            : await client.RemoveAsync(policy.Origin, browser, verified.Attempt.UpstreamId, credentialId, ct);
    }
    public async Task<PasskeyLoginReply> OptionsAsync(string browser, string? completedLoginHandle, CancellationToken ct)
    {
        var policy = await client.GetPolicyAsync(ct);
        if (policy is not { Scope: "CustomerDomain", Origin: not null }) return new("Unavailable");
        Guid? proof = null;
        if (completedLoginHandle is not null)
        {
            var verified = store.Read(completedLoginHandle, browser);
            if (verified is not { Phase: AttemptPhase.Completed, Attempt.Kind: "audkenni" }) return new("Unavailable");
            proof = verified.Attempt.UpstreamId;
        }
        var handle = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (!store.TryAdd(new(handle, browser, Guid.Empty, clock.GetUtcNow().AddMinutes(2),
            proof is null ? "passkey-login" : "passkey-register", policy.Origin))) return new("Unavailable");
        try
        {
            var result = proof is { } id ? await client.RegistrationOptionsAsync(policy.Origin, browser, id, ct)
                : await client.LoginOptionsAsync(policy.Origin, browser, ct);
            if (result.State != "Options" || result.CeremonyId is not { } ceremony || !store.Bind(handle, browser, ceremony))
            { store.Transition(handle, browser, AttemptPhase.Starting, AttemptPhase.Failed); return new("Unavailable") { ErrorCode = result.ErrorCode }; }
            return new("Options", handle, result.OptionsJson);
        }
        catch { store.Transition(handle, browser, AttemptPhase.Starting, AttemptPhase.Failed); throw; }
    }
    public async Task<PasskeyLoginReply> FinishAsync(HttpContext http, string browser, string handle, string credential, CancellationToken ct)
    {
        var entry = store.Read(handle, browser);
        if (entry is null || entry.Attempt.Kind is not ("passkey-login" or "passkey-register")) return new("Expired");
        if (entry.Phase == AttemptPhase.Completed) return new(entry.Attempt.Kind == "passkey-register" ? "Registered" : "Completed", Redirect: entry.Redirect);
        if (!store.Transition(handle, browser, AttemptPhase.Ready, AttemptPhase.Polling)) return new("Unavailable");
        try
        {
            var result = entry.Attempt.Kind == "passkey-register"
                ? await client.FinishRegistrationAsync(entry.Attempt.Origin!, browser, entry.Attempt.UpstreamId, credential, ct)
                : await client.FinishLoginAsync(entry.Attempt.Origin!, browser, entry.Attempt.UpstreamId, credential, ct);
            if (result.State != "Succeeded" || result.Identity is null) return new("Failed") { ErrorCode = result.ErrorCode };
            if (!store.Transition(handle, browser, AttemptPhase.Polling, AttemptPhase.Completing)) return new("Cancelled");
            try
            {
                string? redirect = null;
                if (entry.Attempt.Kind == "passkey-login")
                {
                    redirect = await completion.CompleteAsync(http, result.Identity, handle, ct);
                    if (string.IsNullOrEmpty(redirect) || redirect[0] != '/' || redirect.StartsWith("//") || redirect.Contains('\\') || redirect.Any(char.IsControl))
                        throw new InvalidOperationException("Completion must return a local URL.");
                }
                if (!store.Transition(handle, browser, AttemptPhase.Completing, AttemptPhase.Completed, redirect)) return new("Expired");
                return new(entry.Attempt.Kind == "passkey-register" ? "Registered" : "Completed", Redirect: redirect);
            }
            catch { store.Transition(handle, browser, AttemptPhase.Completing, AttemptPhase.Failed); throw; }
        }
        finally { store.Transition(handle, browser, AttemptPhase.Polling, AttemptPhase.Failed); }
    }
    public async Task<PasskeyLoginReply> CancelAsync(string browser, string handle, CancellationToken ct)
    {
        var entry = store.Read(handle, browser);
        if (entry is null || entry.Attempt.Kind is not ("passkey-login" or "passkey-register")) return new("Expired");
        if (entry.Phase == AttemptPhase.Cancelled) return new("Cancelled");
        if (!store.Transition(handle, browser, AttemptPhase.Ready, AttemptPhase.Cancelled) &&
            !store.Transition(handle, browser, AttemptPhase.Polling, AttemptPhase.Cancelled)) return new("Unavailable");
        await client.CancelAsync(entry.Attempt.Origin!, browser, entry.Attempt.UpstreamId, ct);
        return new("Cancelled");
    }
}
