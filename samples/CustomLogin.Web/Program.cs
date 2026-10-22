using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Vetta.Connector;
using Vetta.Connector.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVettaConnector(options =>
{
    options.BaseUrl = new Uri(builder.Configuration["Vetta:BaseUrl"] ?? "https://localhost:7143/");
    options.ApplicationId = builder.Configuration["Vetta:ApplicationId"] ?? "";
    options.ApiKey = builder.Configuration["Vetta:ApiKey"] ?? "";
});
builder.Services.AddAuthentication("Demo").AddCookie("Demo", options =>
{
    options.Cookie.Name = "__Host-VettaDemo";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(20);
});
builder.Services.AddAuthorization();
builder.Services.AddVettaAuthenticationFlow<DemoCompletion>();
var app = builder.Build();
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapVettaAuthentication();
app.MapGet("/verified", (HttpContext context) => Results.Text("Authenticated in the sample website.")).RequireAuthorization();
app.Run();

// Demo only: real sites resolve their own account, enforce account policy and map claims.
sealed class DemoCompletion : IVettaLoginCompletion
{
    public async Task<string> CompleteAsync(HttpContext context, VerifiedIdentity identity, string completionId, CancellationToken ct)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, identity.Ssn), new Claim(ClaimTypes.Name, identity.Name)
        ], "Demo"));
        await context.SignInAsync("Demo", principal, new AuthenticationProperties { IsPersistent = false });
        return "/verified";
    }
}
