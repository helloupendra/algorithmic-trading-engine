using AlgoTrading.Application.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.Infrastructure.Providers.SimBroker;

/// <summary>
/// The simulated broker registers itself, like every other vendor.
/// </summary>
/// <remarks>
/// Deliberately inert: no hosted service, no feed, no timer, no order routing.
/// Registering it adds an entry to the Connectors page and a client that is
/// used only when someone asks for it.
/// </remarks>
public static class SimBrokerRegistration
{
    public static IServiceCollection AddSimBrokerProvider(
        this IServiceCollection services,
        IConfiguration configuration,
        ProviderCatalogSeed catalog,
        ProviderCredentialFallbacks credentialFallbacks)
    {
        var section = configuration.GetSection("SimBroker");
        services.Configure<SimBrokerSettings>(section);
        services.PostConfigure<SimBrokerSettings>(s =>
        {
            // .env is the fallback for every field: the console cannot hold an
            // app secret or a TOTP secret yet.
            if (string.IsNullOrWhiteSpace(s.ClientId)) s.ClientId = configuration["SIMBROKER_CLIENT_ID"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s.AppId)) s.AppId = configuration["SIMBROKER_APP_ID"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s.AppSecret)) s.AppSecret = configuration["SIMBROKER_APP_SECRET"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s.TotpSecret)) s.TotpSecret = configuration["SIMBROKER_TOTP_SECRET"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s.StaticIp)) s.StaticIp = configuration["SIMBROKER_STATIC_IP"] ?? string.Empty;
            string? url = configuration["SIMBROKER_BASE_URL"];
            if (!string.IsNullOrWhiteSpace(url) && section["BaseUrl"] is null) s.BaseUrl = url;
        });

        catalog.Add(SimBrokerProvider.Descriptor);

        credentialFallbacks.Add(
            SimBrokerProvider.Key,
            section["ClientId"] ?? configuration["SIMBROKER_CLIENT_ID"] ?? string.Empty,
            section["AppSecret"] ?? configuration["SIMBROKER_APP_SECRET"] ?? string.Empty,
            redirectUri: string.Empty);

        services.AddHttpClient(SimBrokerSettings.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddSingleton<SimBrokerClient>();

        return services;
    }
}
