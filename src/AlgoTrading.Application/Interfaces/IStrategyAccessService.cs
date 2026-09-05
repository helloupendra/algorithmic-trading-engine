namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// What one trader may run, and the ceilings that come with it.
/// </summary>
/// <param name="AllowedStrategyNames">
/// Package membership plus per-trader overrides. Empty means they may run nothing.
/// </param>
/// <param name="MaxLotsPerRun">Null when the package sets no ceiling.</param>
/// <param name="MaxConcurrentRuns">
/// The tightest of the package cap, the per-user cap and the platform limit — null
/// when none of them sets one.
/// </param>
/// <param name="AllowedUnderlyings">Empty means every underlying the strategy itself supports.</param>
/// <param name="AllowLiveMode">False keeps this trader on paper.</param>
/// <param name="PackageName">For the message an operator reads when something is refused.</param>
public sealed record StrategyAccess(
    IReadOnlyCollection<string> AllowedStrategyNames,
    int? MaxLotsPerRun,
    int? MaxConcurrentRuns,
    IReadOnlyCollection<string> AllowedUnderlyings,
    bool AllowLiveMode,
    string? PackageName)
{
    /// <summary>An admin: everything, no ceilings of its own.</summary>
    public static StrategyAccess Unrestricted { get; } = new(
        Array.Empty<string>(),
        null,
        null,
        Array.Empty<string>(),
        true,
        null)
    {
        IsUnrestricted = true,
    };

    public bool IsUnrestricted { get; init; }

    /// <summary>True when the package deliberately covers the whole catalog.</summary>
    public bool IncludesAllStrategies { get; init; }

    public bool AllowsStrategy(string strategyName)
        => IsUnrestricted
           || IncludesAllStrategies
           || AllowedStrategyNames.Contains(strategyName, StringComparer.OrdinalIgnoreCase);

    public bool AllowsUnderlying(string underlying)
        => IsUnrestricted
           || AllowedUnderlyings.Count == 0
           || AllowedUnderlyings.Contains(underlying, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Why a deploy was refused, in words an operator can act on.</summary>
public sealed record StrategyAccessDecision(bool Allowed, string? Reason)
{
    public static readonly StrategyAccessDecision Ok = new(true, null);

    public static StrategyAccessDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Resolves and enforces what a trader may run.
/// </summary>
/// <remarks>
/// The same rule as module grants: deny by default, and checked on the server.
/// Filtering the strategy list is a courtesy; the check at deploy time is what
/// actually stops anything.
/// </remarks>
public interface IStrategyAccessService
{
    Task<StrategyAccess> GetAccessAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this trader may start this run, given the strategy, the underlying,
    /// the lots and the mode.
    /// </summary>
    Task<StrategyAccessDecision> CanDeployAsync(
        long userId,
        string strategyName,
        string underlying,
        int lots,
        string mode,
        int currentOpenRuns,
        CancellationToken cancellationToken = default);
}
