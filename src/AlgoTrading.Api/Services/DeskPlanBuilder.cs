// src/AlgoTrading.Api/Services/DeskPlanBuilder.cs
using AlgoTrading.Api.Configuration;
using AlgoTrading.Contracts.Desk;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services;

/// <summary>
/// The morning plan against the desk: every run it asks for, and whether that
/// run is live now (GET /api/Desk/plan).
/// </summary>
/// <remarks>
/// The Desk used to recover the plan from the words of Sentinel's checkup
/// ("All 23 planned runs are live (admin 13, coderforchange 10)"), which says
/// nothing before the first checkup of the day and nothing about which run is
/// missing. This reads the file itself (<see cref="MorningPlan"/>) and matches
/// it the way the morning tally does (scripts/lib/morning_tally.py): a run
/// belongs to the account it runs for, names compare in any case, and it is
/// live when its row says Running and its runner process is alive.
/// </remarks>
public sealed class DeskPlanBuilder
{
    /// <summary>The plan's place in the repository.</summary>
    public static readonly string RelativePath = Path.Combine("config", "morning-plan.txt");

    /// <summary>How many directories above a starting point are searched for <see cref="RelativePath"/>.</summary>
    private const int SearchDepth = 4;

    private readonly TradingDbContext _dbContext;
    private readonly StrategyProcessRegistry _registry;
    private readonly IWebHostEnvironment _environment;
    private readonly DeskOptions _options;

    public DeskPlanBuilder(
        TradingDbContext dbContext,
        StrategyProcessRegistry registry,
        IWebHostEnvironment environment,
        IOptions<DeskOptions> options)
    {
        _dbContext = dbContext;
        _registry = registry;
        _environment = environment;
        _options = options.Value;
    }

    /// <summary>Where the plan was looked for, and the file when one was found.</summary>
    public sealed record Location(string? File, IReadOnlyList<string> Searched);

    /// <summary>
    /// Desk:PlanFile when it is set (relative to the content root); otherwise
    /// config/morning-plan.txt in the content root or up to four levels above
    /// it, then the same from the working directory. The content root is
    /// src/AlgoTrading.Api under a plain <c>dotnet run</c> and may be the
    /// repository root in a published layout, so neither is assumed.
    /// </summary>
    public Location Locate()
    {
        if (!string.IsNullOrWhiteSpace(_options.PlanFile))
        {
            var configured = Path.GetFullPath(Path.Combine(_environment.ContentRootPath, _options.PlanFile));
            return new Location(File.Exists(configured) ? configured : null, new[] { configured });
        }

        var searched = new List<string>();
        foreach (var start in new[] { _environment.ContentRootPath, Directory.GetCurrentDirectory() })
        {
            var directory = string.IsNullOrWhiteSpace(start) ? null : new DirectoryInfo(start);
            for (int depth = 0; depth <= SearchDepth && directory is not null; depth++)
            {
                var candidate = Path.Combine(directory.FullName, RelativePath);
                if (!searched.Contains(candidate)) searched.Add(candidate);
                if (File.Exists(candidate)) return new Location(candidate, searched);
                directory = directory.Parent;
            }
        }

        return new Location(null, searched);
    }

    /// <summary>The plan and its runs; null when there is no plan file to read.</summary>
    public async Task<DeskPlanResponse?> BuildAsync(CancellationToken cancellationToken)
    {
        var location = Locate();
        if (location.File is null) return null;

        var plan = MorningPlan.Parse(await File.ReadAllTextAsync(location.File, cancellationToken));

        var response = new DeskPlanResponse
        {
            File = location.File,
            ModifiedUtc = File.GetLastWriteTimeUtc(location.File),
            Accounts = plan.Accounts.ToList(),
            Warnings = plan.Warnings.ToList(),
            Lines = plan.Lines.Select(l => new DeskPlanLine
            {
                Number = l.Number,
                Text = l.Text,
                Strategy = l.Strategy,
                Underlyings = l.Underlyings.ToList(),
                Lots = l.Lots,
                LegTarget = l.LegTarget,
                LegTargetPoints = l.LegTargetPoints,
                OnlyAccounts = l.OnlyAccounts.ToList()
            }).ToList()
        };

        // Active accounts by name, any case — the morning job's user_id(). The
        // table is small; reading it whole is cheaper than a case-folding query.
        var userIdByName = (await _dbContext.AppUsers.AsNoTracking()
                .Where(u => u.IsActive)
                .Select(u => new { u.Id, u.UserName })
                .ToListAsync(cancellationToken))
            .GroupBy(u => u.UserName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(u => u.Id), StringComparer.OrdinalIgnoreCase);

        var live = await LiveRunsAsync(cancellationToken);

        foreach (var expected in plan.ExpectedRuns())
        {
            long? userId = userIdByName.TryGetValue(expected.Account, out var id) ? id : null;
            long? runId = userId is { } owner
                && live.TryGetValue((owner, expected.Strategy.ToLowerInvariant(), expected.Underlying.ToUpperInvariant()), out var found)
                ? found
                : null;

            response.Runs.Add(new DeskPlanRun
            {
                Account = expected.Account,
                UserId = userId,
                Strategy = expected.Strategy,
                Underlying = expected.Underlying,
                Lots = expected.Lots,
                IsLive = runId.HasValue,
                RunId = runId
            });
        }

        response.Planned = response.Runs.Count;
        response.Live = response.Runs.Count(r => r.IsLive);
        return response;
    }

    /// <summary>
    /// The live runs by (owner, strategy, underlying), the newest where a
    /// restart left two: Running rows whose runner is in the registry.
    /// </summary>
    private async Task<Dictionary<(long, string, string), long>> LiveRunsAsync(CancellationToken cancellationToken)
    {
        var rows = await _dbContext.SimulationRuns.AsNoTracking()
            .Where(r => r.Mode == StrategyRunControl.LivePaperMode && r.Status == StrategyRunControl.RunStatusRunning)
            .Select(r => new { r.Id, r.UserId, r.StrategyName, r.Symbol, r.ParametersJson })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<(long, string, string), long>();
        foreach (var row in rows.OrderBy(r => r.Id))
        {
            var running = _registry.Get(row.Id);
            if (running is null) continue;

            var underlying = LiveRunHistoryBuilder.DeriveUnderlying(running, null, LiveRunParameters.Parse(row.ParametersJson), row.Symbol);
            result[(row.UserId, row.StrategyName.ToLowerInvariant(), underlying)] = row.Id;
        }

        return result;
    }
}
