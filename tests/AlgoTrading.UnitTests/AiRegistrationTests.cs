using System.Reflection;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Infrastructure.Ai;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Every AI tool and scheduled agent can be built by the API's container.
/// </summary>
/// <remarks>
/// They are scoped and resolved late: a tool when a model first asks for it, an
/// agent on the scheduler's first tick. A dependency nobody registered would
/// only show in production, as a failed question or a silent agent. So each
/// constructor's required parameters are checked against the registrations in
/// the source, the way <c>ControllerAuthorizationTests</c> reads the source.
/// </remarks>
public class AiRegistrationTests
{
    /// <summary>What ASP.NET and the generic host provide without a line of ours.</summary>
    private static readonly string[] Framework =
        ["IServiceScopeFactory", "ILogger`1", "IOptionsMonitor`1", "IWebHostEnvironment", "IHttpClientFactory"];

    public static IEnumerable<object[]> Implementations() =>
        typeof(AiAgentScheduler).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false }
                        && (typeof(IAiTool).IsAssignableFrom(t) || typeof(IAiScheduledAgent).IsAssignableFrom(t)))
            .Select(t => new object[] { t.FullName! });

    [Theory]
    [MemberData(nameof(Implementations))]
    public void Every_required_dependency_is_registered(string typeName)
    {
        var type = typeof(AiAgentScheduler).Assembly.GetType(typeName)!;
        string registrations = Registrations();

        // Registered itself, as the interface it serves.
        Assert.Contains(type.Name + ">", registrations);

        var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        foreach (var p in ctor.GetParameters().Where(p => !p.HasDefaultValue))
        {
            string name = p.ParameterType.Name;
            if (Framework.Contains(name)) continue;
            Assert.True(
                registrations.Contains($"<{name}>") || registrations.Contains($".{name}>") || registrations.Contains($"<{name},")
                    || registrations.Contains($"{name}(") || registrations.Contains($".{name}("),
                $"{type.Name} needs {name}, which nothing registers.");
        }
    }

    [Fact]
    public void Every_tool_the_catalog_names_has_an_implementation()
    {
        var names = typeof(AiAgentScheduler).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAiTool).IsAssignableFrom(t))
            .Select(t => (string)t.GetProperty(nameof(IAiTool.Name))!.GetValue(RuntimeHelpersInstance(t))!)
            .ToHashSet();

        Assert.All(AiCatalog.Agents.SelectMany(a => a.Tools ?? []), n => Assert.Contains(n, names));
    }

    /// <summary>The tool's Name without running its constructor's dependencies.</summary>
    private static object RuntimeHelpersInstance(Type t) => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);

    private static string Registrations()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string[] files =
        [
            Path.Combine(dir!.FullName, "src", "AlgoTrading.Api", "Program.cs"),
            Path.Combine(dir.FullName, "src", "AlgoTrading.Infrastructure", "DependencyInjection.cs"),
            Path.Combine(dir.FullName, "src", "AlgoTrading.Infrastructure", "Ai", "AiRegistration.cs"),
        ];

        // Only the lines that register something.
        return string.Join('\n', files.SelectMany(File.ReadAllLines)
            .Where(l => l.Contains(".Add", StringComparison.Ordinal) || l.Contains("services.", StringComparison.Ordinal)));
    }
}
