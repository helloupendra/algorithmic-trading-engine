using AlgoTrading.Application.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>
/// The Dhan connector registers itself, the same way FYERS and TrueData do: a
/// vendor is its own folder plus this file, and the composition root never
/// learns what is inside it.
/// </summary>
public static class DhanRegistration
{
    public static IServiceCollection AddDhanProvider(
        this IServiceCollection services,
        IConfiguration configuration,
        ProviderCatalogSeed catalog,
        ProviderCredentialFallbacks credentialFallbacks)
    {
        var section = configuration.GetSection("Dhan");
        services.Configure<DhanSettings>(section);
        services.PostConfigure<DhanSettings>(s =>
        {
            // Environment variables as the fallback for the two values the
            // console cannot hold yet.
            if (string.IsNullOrWhiteSpace(s.ApiKey)) s.ApiKey = configuration["DHAN_API_KEY"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(s.AccessToken)) s.AccessToken = configuration["DHAN_ACCESS_TOKEN"] ?? string.Empty;
        });

        catalog.Add(DhanProvider.Descriptor);

        // Credentials saved from the console win; this is the appsettings/.env
        // fallback. The two encrypted fields every connector has hold the Dhan
        // client id and the API secret; the API key sits in DhanSettings.
        var settings = new DhanSettings();
        credentialFallbacks.Add(
            DhanProvider.Key,
            section["ClientId"] ?? configuration["DHAN_CLIENT_ID"] ?? string.Empty,
            section["ApiSecret"] ?? configuration["DHAN_API_SECRET"] ?? string.Empty,
            redirectUri: section["RedirectUri"] ?? settings.RedirectUri);

        services.AddHttpClient(DhanProvider.Key, client => client.Timeout = TimeSpan.FromMinutes(2));

        // One pacing budget for the process: Dhan's limits are per account.
        services.AddSingleton<DhanRateGate>();

        services.AddScoped<DhanApiClient>();
        services.AddScoped<IMarketDataProvider, DhanMarketDataProvider>();

        // The daily browser sign-in: resolvable by name for the callback, and as
        // a login flow for the Connectors page's Connect button.
        services.AddScoped<DhanLoginFlow>();
        services.AddScoped<IProviderLoginFlow>(sp => sp.GetRequiredService<DhanLoginFlow>());

        // Not part of the market-data seam (that interface is bars), so they are
        // injected where they are wanted by name.
        services.AddScoped<DhanOptionChainClient>();
        services.AddScoped<DhanInstrumentImporter>();

        return services;
    }
}
