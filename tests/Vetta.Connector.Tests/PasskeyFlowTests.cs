using Microsoft.AspNetCore.Http;
using Vetta.Connector.AspNetCore;
namespace Vetta.Connector.Tests;
public sealed class PasskeyFlowTests
{
    [Fact]
    public async Task Browser_binding_completion_replay_and_cancel_are_enforced()
    {
        var store = new DevelopmentAttemptStore(TimeProvider.System); var client = new Client(); var completion = new Completion();
        var flow = new VettaPasskeyFlow(client,store,completion,TimeProvider.System);
        var options = await flow.OptionsAsync("browser",null,default);
        Assert.Equal("Expired",(await flow.FinishAsync(new DefaultHttpContext(),"other",options.Handle!,"{}",default)).State);
        Assert.Equal(0,client.Finishes);
        Assert.Equal("Completed",(await flow.FinishAsync(new DefaultHttpContext(),"browser",options.Handle!,"{}",default)).State);
        Assert.Equal("Completed",(await flow.FinishAsync(new DefaultHttpContext(),"browser",options.Handle!,"{}",default)).State);
        Assert.Equal(1,completion.Calls); Assert.Equal(1,client.Finishes);
        var next = await flow.OptionsAsync("browser",null,default);
        Assert.Equal("Cancelled",(await flow.CancelAsync("browser",next.Handle!,default)).State);
        Assert.Equal("Unavailable",(await flow.FinishAsync(new DefaultHttpContext(),"browser",next.Handle!,"{}",default)).State);
        Assert.Equal(1,completion.Calls);
    }
    [Fact]
    public async Task Registration_requires_completed_provider_login_in_same_browser()
    {
        var store = new DevelopmentAttemptStore(TimeProvider.System); var client = new Client(); var completion = new Completion();
        var flow = new VettaPasskeyFlow(client,store,completion,TimeProvider.System);
        store.TryAdd(new("proof","browser",Guid.Empty,DateTimeOffset.UtcNow.AddMinutes(2)));
        store.Bind("proof","browser",Guid.NewGuid());
        Assert.Equal("Unavailable",(await flow.OptionsAsync("browser","proof",default)).State);
        store.Transition("proof","browser",AttemptPhase.Ready,AttemptPhase.Completed);
        Assert.Equal("Unavailable",(await flow.OptionsAsync("other","proof",default)).State);
        var options=await flow.OptionsAsync("browser","proof",default);
        Assert.Equal("Options",options.State);
        Assert.Equal("Registered",(await flow.FinishAsync(new DefaultHttpContext(),"browser",options.Handle!,"{}",default)).State);
        Assert.Equal(0,completion.Calls);
    }
    [Fact]
    public async Task Cancellation_during_verification_prevents_late_sign_in()
    {
        var store=new DevelopmentAttemptStore(TimeProvider.System);var client=new Client();var completion=new Completion();
        var flow=new VettaPasskeyFlow(client,store,completion,TimeProvider.System);
        var options=await flow.OptionsAsync("browser",null,default);
        client.Pending=new();
        var finish=flow.FinishAsync(new DefaultHttpContext(),"browser",options.Handle!,"{}",default);
        Assert.Equal("Cancelled",(await flow.CancelAsync("browser",options.Handle!,default)).State);
        client.Pending.SetResult(new("Succeeded",Identity:new("Fixture","1234567890")));
        Assert.Equal("Cancelled",(await finish).State);Assert.Equal(0,completion.Calls);
    }
    private sealed class Completion:IVettaLoginCompletion
    {
        public int Calls;
        public Task<string> CompleteAsync(HttpContext c,VerifiedIdentity i,string id,CancellationToken ct){Calls++;return Task.FromResult("/account");}
    }
    internal sealed class Client:IVettaPasskeyClient
    {
        public int Finishes; public TaskCompletionSource<PasskeyResponse>? Pending;
        public Task<PasskeyPolicy?> GetPolicyAsync(CancellationToken ct=default)=>Task.FromResult<PasskeyPolicy?>(new("CustomerDomain","https://customer.example",false));
        public Task<PasskeyResponse> LoginOptionsAsync(string o,string b,CancellationToken ct=default)=>Task.FromResult(new PasskeyResponse("Options",Guid.NewGuid(),"{}"));
        public Task<PasskeyResponse> RegistrationOptionsAsync(string o,string b,Guid p,CancellationToken ct=default)=>LoginOptionsAsync(o,b,ct);
        public Task<PasskeyResponse> FinishLoginAsync(string o,string b,Guid i,string j,CancellationToken ct=default){Finishes++;return Pending?.Task??Task.FromResult(new PasskeyResponse("Succeeded",Identity:new("Fixture","1234567890")));}
        public Task<PasskeyResponse> FinishRegistrationAsync(string o,string b,Guid i,string j,CancellationToken ct=default)=>FinishLoginAsync(o,b,i,j,ct);
        public Task<PasskeyResponse> CancelAsync(string o,string b,Guid i,CancellationToken ct=default)=>Task.FromResult(new PasskeyResponse("Cancelled"));
    }
}
