using AlgoTrading.Application.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.Infrastructure.Providers.Replay;

/// <summary>The replay connector registers itself. No settings, no credentials.</summary>
public static class ReplayRegistration
{
    public static IServiceCollection AddReplayProvider(
        this IServiceCollection services,
        ProviderCatalogSeed catalog)
    {
        catalog.Add(ReplayProvider.Descriptor);
        services.AddScoped<IMarketDataProvider, ReplayMarketDataProvider>();

        return services;
    }
}
