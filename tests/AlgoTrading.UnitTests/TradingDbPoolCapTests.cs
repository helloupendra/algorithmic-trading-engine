using AlgoTrading.Infrastructure;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The API's Npgsql pool is capped so one process can never hold every
/// Postgres connection (28 Sep: 53300 "too many clients" during a deploy overlap).
/// </summary>
public class TradingDbPoolCapTests
{
    private const string Plain = "Host=localhost;Port=5432;Database=algotrading;Username=postgres;Password=x";

    [Fact]
    public void A_string_without_a_pool_size_gets_the_cap()
    {
        var capped = TradingDbConnectionString.WithPoolCap(Plain);

        Assert.Equal(TradingDbConnectionString.DefaultMaxPoolSize, new NpgsqlConnectionStringBuilder(capped).MaxPoolSize);
        Assert.Equal("algotrading", new NpgsqlConnectionStringBuilder(capped).Database);
    }

    [Theory]
    [InlineData("Maximum Pool Size=80")]
    [InlineData("MaxPoolSize=80")]
    [InlineData("maximum pool size=80")]
    public void An_explicit_pool_size_wins(string setting)
    {
        var capped = TradingDbConnectionString.WithPoolCap($"{Plain};{setting}");

        Assert.Equal(80, new NpgsqlConnectionStringBuilder(capped).MaxPoolSize);
    }

    [Fact]
    public void The_registered_context_uses_the_capped_string()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TradingDb"] = Plain })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        Assert.Equal(TradingDbConnectionString.DefaultMaxPoolSize,
            new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()).MaxPoolSize);
    }
}
