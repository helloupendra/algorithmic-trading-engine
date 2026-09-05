namespace AlgoTrading.Domain.Constants;

/// <summary>One thing a trader can be allowed to do.</summary>
/// <param name="Key">Stable id stored in <c>user_module_grants</c>; never change it.</param>
public sealed record PlatformModule(string Key, string Name, string Description);

/// <summary>
/// The grantable modules, as the server knows them.
/// </summary>
/// <remarks>
/// This list is the authority, not the console's navigation. Hiding a menu entry
/// is not access control — a trader can type the URL — so every endpoint a module
/// covers carries <c>[RequireModule]</c> and is checked here.
/// <para>
/// Admins are not granted modules; they have all of them by definition.
/// </para>
/// </remarks>
public static class PlatformModules
{
    /// <summary>Deploy, monitor and stop their own strategy runs.</summary>
    public const string Strategies = "strategies";

    /// <summary>Run and read backtests.</summary>
    public const string Backtesting = "backtesting";

    /// <summary>Charts, option chain, watchlist, movers and news — read-only market views.</summary>
    public const string MarketData = "market-data";

    public static readonly IReadOnlyList<PlatformModule> All = new[]
    {
        new PlatformModule(
            Strategies,
            "Strategies",
            "Deploy, monitor and stop their own live runs. Without it a trader can sign in but cannot trade."),
        new PlatformModule(
            Backtesting,
            "Backtesting",
            "Run backtests over stored history and read the results."),
        new PlatformModule(
            MarketData,
            "Market data",
            "Charts, option chain, watchlist, movers and news. Read-only."),
    };

    public static bool IsKnown(string? key)
        => key is not null && All.Any(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
}
