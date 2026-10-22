using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Vetta.Connector;

public static class Registration
{
    public static IServiceCollection AddVettaConnector(this IServiceCollection services,
        Action<VettaConnectorOptions> configure)
    {
        services.AddOptions<VettaConnectorOptions>().Configure(configure)
            .Validate(options => { options.Validate(); return true; }).ValidateOnStart();
        services.AddHttpClient<IVettaClient, VettaClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false
            })
            .RemoveAllLoggers();
        services.AddHttpClient<IVettaPasskeyClient, VettaPasskeyClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
        return services;
    }
}
