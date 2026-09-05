using AlgoTrading.Application.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.Infrastructure.Providers.Fyers;

/// <summary>
/// The FYERS connector registers itself. Adding a vendor is this file plus its
/// adapters — the platform's own composition root never learns a vendor name.
/// </summary>
public static class FyersRegistration
{
    public static IServiceCollection AddFyersProvider(
        this IServiceCollection services,
        IConfiguration configuration,
        ProviderCatalog catalog,
        ProviderCredentialFallbacks credentialFallbacks)
    {
        var section = configuration.GetSection("Fyers");

        services.Configure<FyersSettings>(section);

        catalog.Add(FyersProvider.Descriptor);

        // Credentials saved from the console win; this is the appsettings/.env
        // fallback that keeps existing installs working unchanged.
        credentialFallbacks.Add(
            FyersProvider.Key,
            section["ClientId"] ?? string.Empty,
            section["SecretKey"] ?? string.Empty,
            section["RedirectUri"] ?? string.Empty);

        services.AddHttpClient(FyersProvider.Key);

        services.AddScoped<IMarketDataProvider, FyersMarketDataProvider>();
        services.AddScoped<IBrokerProvider, FyersBrokerProvider>();

        return services;
    }
}
