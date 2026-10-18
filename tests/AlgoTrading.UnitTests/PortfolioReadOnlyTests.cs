using System.Reflection;
using System.Text.RegularExpressions;
using AlgoTrading.Infrastructure.Portfolio;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Portfolio page reads a user's real broker account. The clients that do it must not be able to do anything
/// else: no order, no modification, no cancellation, no conversion, no exit. Checked twice, because the failure being
/// guarded against is one added line: the source of every file they are built from, and the clients' own surface.
/// </summary>
public class PortfolioReadOnlyTests
{
    /// <summary>Every call the portfolio clients may make, and nothing else.</summary>
    private static readonly HashSet<string> ReadPaths = new(StringComparer.Ordinal)
    {
        "/api/v3/generate-authcode", "/api/v3/validate-authcode", "/api/v3/profile", "/api/v3/holdings", "/api/v3/positions", "/api/v3/funds",
        "/v2/profile", "/v2/holdings", "/v2/positions", "/v2/fundlimit",
        "/rest/secure/angelbroking/user/v1/getProfile", "/rest/secure/angelbroking/portfolio/v1/getAllHolding",
        "/rest/secure/angelbroking/order/v1/getPosition", "/rest/secure/angelbroking/user/v1/getRMS",
    };

    /// <summary>
    /// The brokers' order and trade-management calls (FYERS /orders, /orders/sync, /multi-order, /positions exit and
    /// convert; Dhan /v2/orders, super and forever orders, /v2/positions/convert, the kill switch; Angel placeOrder,
    /// modifyOrder, cancelOrder, convertPosition, GTT) and the words for them.
    /// </summary>
    private static readonly Regex OrderLike = new(
        @"placeorder|modifyorder|cancelorder|convertposition|exitposition|/orders|/order/|multi-order|multiorder|super/|forever/|killswitch|/gtt|gtt/|/trades|tradebook|orderbook|/exit|/convert|HttpMethod\.(Put|Patch|Delete)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void No_portfolio_client_source_names_an_order_call()
    {
        var dir = Path.Combine(PortfolioTestKit.RepoRoot(), "src", "AlgoTrading.Infrastructure", "Portfolio");
        var files = Directory.GetFiles(dir, "*.cs");
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        foreach (string file in files)
        {
            foreach (var (line, i) in File.ReadLines(file).Select((l, i) => (l, i)))
            {
                // Angel files positions under order/v1: that one read is allowed by its full path, nothing else under it.
                string code = line.Replace("/rest/secure/angelbroking/order/v1/getPosition", string.Empty, StringComparison.Ordinal);
                if (OrderLike.IsMatch(code)) offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "order-like calls in the portfolio clients:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Every_path_a_portfolio_client_declares_is_a_read()
    {
        var clients = typeof(IPortfolioBroker).Assembly.GetTypes()
            .Where(t => typeof(IPortfolioBroker).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .ToList();
        Assert.Equal(3, clients.Count);

        var declared = clients
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string) && f.Name.EndsWith("Path", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(14, declared.Count);
        Assert.All(declared, path => Assert.Contains(path, ReadPaths));
    }

    [Fact]
    public void No_portfolio_client_has_a_method_named_for_trading()
    {
        var verbs = new[] { "Order", "Place", "Modify", "Cancel", "Exit", "Convert", "Square", "Trade" };
        var offenders = typeof(IPortfolioBroker).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(IPortfolioBroker).Namespace)
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => $"{t.Name}.{m.Name}"))
            .Where(name => verbs.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }
}
