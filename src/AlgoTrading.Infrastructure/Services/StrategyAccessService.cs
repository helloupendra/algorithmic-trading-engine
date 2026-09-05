using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Resolves a trader's package and overrides into one answer, and enforces it at
/// deploy time.
/// </summary>
public class StrategyAccessService : IStrategyAccessService
{
    private readonly TradingDbContext _dbContext;
    private readonly IRiskLimitsStore _riskLimits;

    public StrategyAccessService(TradingDbContext dbContext, IRiskLimitsStore riskLimits)
    {
        _dbContext = dbContext;
        _riskLimits = riskLimits;
    }

    public async Task<StrategyAccess> GetAccessAsync(long userId, CancellationToken cancellationToken = default)
    {
        var user = await _dbContext.AppUsers
            .AsNoTracking()
            .Include(x => x.StrategyPackage!)
                .ThenInclude(p => p.Items)
            .Include(x => x.StrategyGrants)
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);

        if (user is null)
        {
            return new StrategyAccess(Array.Empty<string>(), null, null, Array.Empty<string>(), false, null);
        }

        // Admins and the engine's own account are not on a plan.
        if (string.Equals(user.Role, UserRoles.Admin, StringComparison.Ordinal) ||
            string.Equals(user.Role, UserRoles.Service, StringComparison.Ordinal))
        {
            return StrategyAccess.Unrestricted;
        }

        var package = user.StrategyPackage is { IsEnabled: true } ? user.StrategyPackage : null;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (package is not null)
        {
            foreach (var item in package.Items)
            {
                names.Add(item.StrategyName);
            }
        }

        // Overrides are additive: the exception that avoids cloning a package to
        // add one strategy for one person.
        foreach (var grant in user.StrategyGrants)
        {
            names.Add(grant.StrategyName);
        }

        var underlyings = (package?.AllowedUnderlyingsCsv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToUpperInvariant())
            .ToList();

        // Three caps can disagree — the package's, the account's and the
        // platform's. The tightest wins, because each was set to stop something.
        var caps = new List<int>();
        if (package?.MaxConcurrentRuns is > 0) caps.Add(package.MaxConcurrentRuns.Value);
        if (user.MaxConcurrentRuns is > 0) caps.Add(user.MaxConcurrentRuns.Value);

        var platformCap = _riskLimits.GetLimits().MaxConcurrentRuns;
        if (platformCap > 0) caps.Add(platformCap);

        return new StrategyAccess(
            names,
            package?.MaxLotsPerRun,
            caps.Count > 0 ? caps.Min() : null,
            underlyings,
            package?.AllowLiveMode ?? false,
            package?.Name)
        {
            IncludesAllStrategies = package?.IncludesAllStrategies ?? false,
        };
    }

    public async Task<StrategyAccessDecision> CanDeployAsync(
        long userId,
        string strategyName,
        string underlying,
        int lots,
        string mode,
        int currentOpenRuns,
        CancellationToken cancellationToken = default)
    {
        var access = await GetAccessAsync(userId, cancellationToken);

        if (access.IsUnrestricted) return StrategyAccessDecision.Ok;

        if (!access.AllowsStrategy(strategyName))
        {
            return StrategyAccessDecision.Deny(
                access.PackageName is null
                    ? $"Your account has no strategy package, so {strategyName} is not available to you. Ask an admin to assign one."
                    : $"{strategyName} is not in your package ({access.PackageName}). Ask an admin to add it.");
        }

        if (!access.AllowsUnderlying(underlying))
        {
            return StrategyAccessDecision.Deny(
                $"Your package allows {string.Join(", ", access.AllowedUnderlyings)}, not {underlying}.");
        }

        if (access.MaxLotsPerRun is int maxLots && lots > maxLots)
        {
            return StrategyAccessDecision.Deny(
                $"Your package allows at most {maxLots} lot(s) per run; this run asks for {lots}.");
        }

        if (!access.AllowLiveMode && !string.Equals(mode, "LivePaper", StringComparison.OrdinalIgnoreCase))
        {
            return StrategyAccessDecision.Deny(
                "Your package is paper-trading only. Ask an admin before running with real money.");
        }

        if (access.MaxConcurrentRuns is int maxRuns && currentOpenRuns >= maxRuns)
        {
            return StrategyAccessDecision.Deny(
                $"You already have {currentOpenRuns} run(s) open and your limit is {maxRuns}. Stop one first.");
        }

        return StrategyAccessDecision.Ok;
    }
}
