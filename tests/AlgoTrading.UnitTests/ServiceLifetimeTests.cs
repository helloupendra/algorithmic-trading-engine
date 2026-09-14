using AlgoTrading.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// No singleton may hold a scoped service.
///
/// On 2026-09-14 the API would not start on a developer machine: the singleton
/// <c>TrueDataTokenStore</c> took the scoped <c>IBrokerCredentialsProvider</c>
/// (and with it a DbContext) in its constructor. Development validates scopes
/// and refused to build the container; Production does not, so the server ran
/// with one DbContext shared across threads for the life of the process. This
/// reads every singleton's constructor instead of waiting for a host to object.
/// </summary>
public class ServiceLifetimeTests
{
    [Fact]
    public void No_infrastructure_singleton_takes_a_scoped_dependency()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:TradingDb"] = "Host=localhost;Database=test;Username=test;Password=test",
                ["ConnectionStrings:Redis"] = "localhost:6379",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(configuration);

        var scoped = services
            .Where(d => d.Lifetime == ServiceLifetime.Scoped)
            .Select(d => d.ServiceType)
            .ToHashSet();

        // A type registered both ways (rare) is not a captive by construction.
        scoped.ExceptWith(services.Where(d => d.Lifetime != ServiceLifetime.Scoped).Select(d => d.ServiceType));

        var captives = new List<string>();
        foreach (var singleton in services.Where(d => d.Lifetime == ServiceLifetime.Singleton && d.ImplementationType is not null))
        {
            foreach (var ctor in singleton.ImplementationType!.GetConstructors())
            {
                foreach (var parameter in ctor.GetParameters().Where(p => scoped.Contains(p.ParameterType)))
                {
                    captives.Add($"{singleton.ImplementationType.Name} takes scoped {parameter.ParameterType.Name}");
                }
            }
        }

        Assert.True(captives.Count == 0, "Singletons holding scoped services:\n" + string.Join("\n", captives));
    }
}
