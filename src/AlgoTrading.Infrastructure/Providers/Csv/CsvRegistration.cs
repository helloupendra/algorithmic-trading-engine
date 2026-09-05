using AlgoTrading.Application.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.Infrastructure.Providers.Csv;

/// <summary>The CSV connector registers itself. A directory, no credentials.</summary>
public static class CsvRegistration
{
    public static IServiceCollection AddCsvProvider(
        this IServiceCollection services,
        IConfiguration configuration,
        ProviderCatalog catalog)
    {
        services.Configure<CsvProviderSettings>(configuration.GetSection("Providers:Csv"));

        catalog.Add(CsvProvider.Descriptor);
        services.AddScoped<IMarketDataProvider, CsvMarketDataProvider>();

        return services;
    }
}
