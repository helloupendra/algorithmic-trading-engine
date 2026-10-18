using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.Infrastructure.Portfolio;

/// <summary>The Portfolio page's broker clients, their settings and the token vault.</summary>
public static class PortfolioRegistration
{
    public static IServiceCollection AddPortfolioBrokers(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<PortfolioSettings>(configuration.GetSection(PortfolioSettings.Section));
        services.AddSingleton<PortfolioTokenVault>();
        services.AddSingleton<AngelPortfolioPacer>();

        // No request logging on these clients at all: a user's token travels in their headers, and the logging
        // handler's request lines are one configuration change away from api.log.
        foreach (string name in new[] { FyersPortfolioClient.HttpClientName, DhanPortfolioClient.HttpClientName, AngelPortfolioClient.HttpClientName })
        {
            services.AddHttpClient(name, client => client.Timeout = TimeSpan.FromSeconds(30)).RemoveAllLoggers();
        }

        services.AddScoped<FyersPortfolioClient>();
        services.AddScoped<DhanPortfolioClient>();
        services.AddScoped<AngelPortfolioClient>();
        services.AddScoped<IPortfolioBroker>(sp => sp.GetRequiredService<FyersPortfolioClient>());
        services.AddScoped<IPortfolioBroker>(sp => sp.GetRequiredService<DhanPortfolioClient>());
        services.AddScoped<IPortfolioBroker>(sp => sp.GetRequiredService<AngelPortfolioClient>());
        return services;
    }
}
