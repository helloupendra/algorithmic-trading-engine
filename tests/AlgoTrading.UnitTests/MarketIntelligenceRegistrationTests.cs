using AlgoTrading.Infrastructure;
using AlgoTrading.Infrastructure.Services.MarketIntelligence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The recorders as the API builds them. The API cannot be started in a test
/// (it would open live feeds), so the container is built from the same
/// registration code and each service resolved in a scope, as a hosted
/// service does per run. A missing registration would otherwise first show up
/// as a recorder that never runs, logged once at start-up.
/// </summary>
public class MarketIntelligenceRegistrationTests
{
    [Fact]
    public void Every_recorder_and_the_queries_resolve_from_the_infrastructure_registrations()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TradingDb"] = "Host=localhost;Database=test;Username=test;Password=test",
                ["ConnectionStrings:Redis"] = "localhost:6379",
                ["MarketIntelligence:BackfillEnabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();
        T Get<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

        Assert.NotNull(Get<NewsRecorder>());
        Assert.NotNull(Get<CorporateFilingsRecorder>());
        Assert.NotNull(Get<GlobalMarketsRecorder>());
        Assert.NotNull(Get<BreadthRecorder>());
        Assert.NotNull(Get<DailyBackfillRunner>());
        Assert.NotNull(Get<MarketIntelligenceQueries>());
        Assert.Same(Get<NseWebClient>(), Get<NseWebClient>());
        Assert.Equal(TimeSpan.FromSeconds(1), Get<NseRequestPacer>().MinimumGap);

        // The switches bind from the section; the rest keep their "on" default.
        var options = Get<IOptions<MarketIntelligenceOptions>>().Value;
        Assert.False(options.BackfillEnabled);
        Assert.True(options.NewsEnabled);
    }
}
