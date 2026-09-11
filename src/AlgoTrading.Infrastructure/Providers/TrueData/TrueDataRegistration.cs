using AlgoTrading.Application.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.Infrastructure.Providers.TrueData;

/// <summary>
/// The TrueData connector registers itself, the same way FYERS does: adding a
/// vendor is its own folder plus this file, and the platform's composition root
/// never learns a vendor name.
/// </summary>
public static class TrueDataRegistration
{
    public static IServiceCollection AddTrueDataProvider(
        this IServiceCollection services,
        IConfiguration configuration,
        ProviderCatalogSeed catalog,
        ProviderCredentialFallbacks credentialFallbacks)
    {
        var section = configuration.GetSection("TrueData");

        services.Configure<TrueDataSettings>(section);

        catalog.Add(TrueDataProvider.Descriptor);

        // Credentials saved from the console win; this is the appsettings/.env
        // fallback. TrueData signs in with a username and a password, which sit
        // in the same two encrypted fields every other connector uses — there is
        // no redirect URL because nothing here goes through a browser.
        credentialFallbacks.Add(
            TrueDataProvider.Key,
            section["Username"] ?? configuration["TRUEDATA_USERNAME"] ?? string.Empty,
            section["Password"] ?? configuration["TRUEDATA_PASSWORD"] ?? string.Empty,
            redirectUri: string.Empty);

        services.AddHttpClient(TrueDataProvider.Key);

        // Singleton: one bearer token for the process. Per-request tokens would
        // be a sign-in per request, which is how a vendor starts refusing.
        services.AddSingleton<TrueDataTokenStore>();

        services.AddScoped<IMarketDataProvider, TrueDataMarketDataProvider>();

        // The chain is not part of the market-data seam — that interface is
        // bars — so it is injected where it is wanted by name.
        services.AddScoped<TrueDataChainClient>();

        return services;
    }
}
