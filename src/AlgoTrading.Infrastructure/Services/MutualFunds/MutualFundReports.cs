using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MutualFunds;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services.MutualFunds.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>
/// Reads what one fund report needs (the scheme's NAVs, its other plan's, the benchmark's values, its TER, its
/// row in the nightly ranking and its peers', the backfill's progress) and builds it with
/// <see cref="MutualFundReportBuilder"/>. About ten indexed reads; no whole-table scans.
/// </summary>
public sealed class MutualFundReports
{
    public const int MaxCompare = 4;

    private readonly TradingDbContext _db;
    private readonly IProcessSettingsStore _settings;
    private readonly MutualFundsOptions _options;

    public MutualFundReports(TradingDbContext db, IProcessSettingsStore settings, IOptions<MutualFundsOptions> options)
    {
        _db = db;
        _settings = settings;
        _options = options.Value;
    }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The report, or the reason there is none (no such scheme, or no NAV on or before the day).</summary>
    public async Task<(MutualFundReportResponse? Report, string? NotFound)> ReportAsync(int code, DateOnly? asOf, CancellationToken ct)
    {
        var scheme = await _db.MutualFundSchemes.AsNoTracking().FirstOrDefaultAsync(s => s.SchemeCode == code, ct);
        if (scheme is null) return (null, $"No scheme {code} in the master.");
        var navs = await NavsAsync(code, ct);
        if (navs.IsEmpty) return (null, $"Scheme {code} has no NAVs stored yet.");
        if (asOf is { } day && navs.Until(day).IsEmpty) return (null, $"Scheme {code} has no NAV on or before {FundAnalytics.Day(day)}; its NAVs start on {FundAnalytics.Day(navs.First)}.");

        var inputs = await InputsAsync(scheme, navs, asOf, ct);
        return (MutualFundReportBuilder.Build(inputs), null);
    }

    /// <summary>2 to 4 reports ending on the latest NAV day all the schemes have.</summary>
    public async Task<(MutualFundCompareResponse? Compare, string? Problem)> CompareAsync(IReadOnlyList<int> codes, CancellationToken ct)
    {
        var distinct = codes.Distinct().ToList();
        if (distinct.Count is < 2 or > MaxCompare) return (null, $"Compare takes 2 to {MaxCompare} different scheme codes.");

        var loaded = new List<(MutualFundScheme Scheme, NavSeries Navs)>();
        foreach (int code in distinct)
        {
            var scheme = await _db.MutualFundSchemes.AsNoTracking().FirstOrDefaultAsync(s => s.SchemeCode == code, ct);
            if (scheme is null) return (null, $"No scheme {code} in the master.");
            var navs = await NavsAsync(code, ct);
            if (navs.IsEmpty) return (null, $"Scheme {code} has no NAVs stored yet.");
            loaded.Add((scheme, navs));
        }

        var common = loaded.Min(l => l.Navs.Last);
        var response = new MutualFundCompareResponse { AsOf = common, Disclaimer = MutualFundReportBuilder.Disclaimer };
        if (loaded.Any(l => l.Navs.Last > common))
        {
            var limiting = loaded.Where(l => l.Navs.Last == common).Select(l => $"{l.Scheme.SchemeName} ({l.Scheme.SchemeCode})");
            response.Notes.Add($"Every report ends on {FundAnalytics.Day(common)}, the newest NAV day all the schemes have: "
                               + $"{string.Join(", ", limiting)} has no later NAV.");
        }

        foreach (var (scheme, navs) in loaded)
        {
            var report = MutualFundReportBuilder.Build(await InputsAsync(scheme, navs, common, ct));
            if (report is not null) response.Funds.Add(report);
        }

        return (response, null);
    }

    private async Task<NavSeries> NavsAsync(int code, CancellationToken ct) =>
        new((await _db.MutualFundNavs.AsNoTracking()
                .Where(n => n.SchemeCode == code)
                .OrderBy(n => n.Date)
                .Select(n => new { n.Date, n.Nav })
                .ToListAsync(ct))
            .Select(n => new NavPoint(n.Date, (double)n.Nav)));

    private async Task<MutualFundReportInputs> InputsAsync(MutualFundScheme scheme, NavSeries navs, DateOnly? asOf, CancellationToken ct)
    {
        var dto = ToDto(scheme);
        var category = MutualFundCategories.Parse(scheme.Category);
        var choice = MutualFundBenchmarks.Choose(scheme.SchemeName, category);
        var end = asOf is { } day ? navs.Until(day).Last : navs.Last;

        NavSeries? benchmark = null;
        if (choice.Index is { } index)
        {
            benchmark = new NavSeries((await _db.MutualFundBenchmarkValues.AsNoTracking()
                    .Where(v => v.IndexName == index.Key)
                    .OrderBy(v => v.Date)
                    .Select(v => new { v.Date, v.Value })
                    .ToListAsync(ct))
                .Select(v => new NavPoint(v.Date, (double)v.Value)));
        }

        var (other, otherNavs, otherMissing) = await OtherPlanAsync(scheme, ct);
        var backfill = await MutualFundSettings.BackfillAsync(_settings, ct);
        var analytics = await MutualFundAnalyticsSettings.ReadAsync(_settings, ct);

        var stats = await _db.MutualFundSchemeStats.AsNoTracking().FirstOrDefaultAsync(s => s.SchemeCode == scheme.SchemeCode, ct);
        var peers = stats is null
            ? []
            : await _db.MutualFundSchemeStats.AsNoTracking().Where(s => s.PeerKey == stats.PeerKey && s.Plan == stats.Plan).ToListAsync(ct);
        DateTime? computed = analytics.Stats is { Ok: true } run ? run.FinishedUtc
            : await _db.MutualFundSchemeStats.AsNoTracking().MaxAsync(s => (DateTime?)s.ComputedUtc, ct);

        var (ter, terMissing) = await TerAsync(scheme, end, ct);

        return new MutualFundReportInputs
        {
            Scheme = dto,
            Navs = navs,
            AsOf = asOf,
            OtherPlan = other is null ? null : ToDto(other),
            OtherPlanNavs = otherNavs,
            OtherPlanMissing = otherMissing,
            Benchmark = choice,
            BenchmarkValues = benchmark,
            BackfillCursor = backfill.Cursor,
            BackfillDone = backfill.State == "done" || (backfill.Cursor is { } cursor && cursor <= backfill.From),
            Stats = stats,
            Peers = peers,
            StatsComputedUtc = computed,
            Ter = ter,
            TerMissing = terMissing,
            RiskFreePct = _options.RiskFreeRatePct,
            RiskFreeSource = _options.RiskFreeRateSource,
            RiskFreeAsOf = _options.RiskFreeRateAsOf,
            NowUtc = Clock(),
        };
    }

    /// <summary>
    /// The same fund's other plan: same AMC, base name, category and option, the other of direct and regular.
    /// None when the plan is not stated, or when two schemes would fit (AMFI lists a few look-alikes).
    /// </summary>
    private async Task<(MutualFundScheme? Other, NavSeries? Navs, string? Missing)> OtherPlanAsync(MutualFundScheme scheme, CancellationToken ct)
    {
        if (scheme.Plan is not ("direct" or "regular"))
            return (null, null, "The scheme's plan is not stated (an ETF, or a plan from before direct plans existed in 2013), so there is no direct or regular plan to set beside it.");

        string otherPlan = scheme.Plan == "direct" ? "regular" : "direct";
        var candidates = await _db.MutualFundSchemes.AsNoTracking()
            .Where(s => s.Amc == scheme.Amc && s.SchemeName == scheme.SchemeName && s.Category == scheme.Category
                        && s.Option == scheme.Option && s.Plan == otherPlan && s.SchemeCode != scheme.SchemeCode)
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return (null, null, $"AMFI lists no {otherPlan} plan of \"{scheme.SchemeName}\" with the same option.");
        if (candidates.Count > 1)
            return (null, null, $"AMFI lists {candidates.Count} {otherPlan}-plan schemes named \"{scheme.SchemeName}\" with the same option ({string.Join(", ", candidates.Select(c => c.SchemeCode))}), so the gap is not computed.");

        var other = candidates[0];
        return (other, await NavsAsync(other.SchemeCode, ct), null);
    }

    /// <summary>
    /// The TER in effect on or before <paramref name="asOf"/> (within 45 days) for the TER scheme whose name matches
    /// this scheme's base name, word for word. None when no TER scheme or two match.
    /// </summary>
    private async Task<(MutualFundTer? Ter, string? Missing)> TerAsync(MutualFundScheme scheme, DateOnly asOf, CancellationToken ct)
    {
        bool any = await _db.MutualFundTers.AsNoTracking().AnyAsync(ct);
        if (!any) return (null, "TER not collected yet: the TER sync reads AMFI's disclosure after 23:45 IST.");

        string key = MutualFundNames.Key(scheme.SchemeName);
        var since = asOf.AddDays(-45);
        var rows = await _db.MutualFundTers.AsNoTracking()
            .Where(t => t.NameKey == key && t.Date <= asOf && t.Date >= since)
            .OrderByDescending(t => t.Date)
            .Take(40)
            .ToListAsync(ct);
        if (rows.Count == 0)
            return (null, $"No TER found: AMFI's TER disclosure has no scheme named \"{scheme.SchemeName}\" with a TER in the 45 days to {FundAnalytics.Day(asOf)}.");
        var schemes = rows.Select(r => r.NsdlSchemeCode).Distinct().ToList();
        if (schemes.Count > 1)
            return (null, $"No TER shown: {schemes.Count} schemes in AMFI's TER disclosure are named \"{scheme.SchemeName}\".");
        return (rows[0], null);
    }

    /// <summary>What the report's jobs have collected, for the coverage page.</summary>
    public async Task<MutualFundAnalyticsCoverageDto> CoverageAsync(MutualFundBenchmarkSync benchmarks, CancellationToken ct)
    {
        var state = await MutualFundAnalyticsSettings.ReadAsync(_settings, ct);
        var stored = await benchmarks.CoverageAsync(ct);
        var latestTer = await _db.MutualFundTers.AsNoTracking().MaxAsync(t => (DateOnly?)t.Date, ct);
        int terSchemes = latestTer is { } terDay
            ? await _db.MutualFundTers.AsNoTracking().Where(t => t.Date == terDay).CountAsync(ct)
            : 0;
        var statsAsOf = await _db.MutualFundSchemeStats.AsNoTracking().MaxAsync(s => (DateOnly?)s.AsOfDate, ct);

        return new MutualFundAnalyticsCoverageDto
        {
            Benchmarks = stored.Select(s => new MutualFundBenchmarkCoverageDto
            {
                Index = s.Index,
                Name = MutualFundBenchmarks.Find(s.Index)?.Name ?? s.Index,
                Rows = s.Rows,
                FirstDate = s.FirstDate,
                LastDate = s.LastDate,
                HistoryComplete = state.BenchmarkHistory.TryGetValue(s.Index, out var h) && h.Complete,
            }).ToList(),
            BenchmarkSync = Dto(state.Benchmarks),
            TerLatestDate = latestTer,
            TerSchemes = terSchemes,
            TerSync = Dto(state.Ter),
            StatsAsOf = statsAsOf,
            StatsSchemes = await _db.MutualFundSchemeStats.AsNoTracking().CountAsync(ct),
            StatsComputedUtc = await _db.MutualFundSchemeStats.AsNoTracking().MaxAsync(s => (DateTime?)s.ComputedUtc, ct) is { } at
                ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
                : null,
            StatsRun = Dto(state.Stats),
            Requested = state.Requested.ToList(),
        };
    }

    public static MutualFundJobRunDto? Dto(MutualFundJobRun? run) => run is null
        ? null
        : new MutualFundJobRunDto
        {
            Trigger = run.Trigger,
            StartedUtc = DateTime.SpecifyKind(run.StartedUtc, DateTimeKind.Utc),
            FinishedUtc = DateTime.SpecifyKind(run.FinishedUtc, DateTimeKind.Utc),
            Ok = run.Ok,
            Message = run.Message,
            Reached = run.Reached,
        };

    public static MutualFundSchemeDto ToDto(MutualFundScheme s) => new()
    {
        SchemeCode = s.SchemeCode,
        SchemeName = s.SchemeName,
        Amc = s.Amc,
        Category = s.Category,
        Plan = s.Plan,
        Option = s.Option,
        OptionLabel = s.OptionLabel,
        IsinGrowth = s.IsinGrowth,
        IsinReinvest = s.IsinReinvest,
        FirstNavDate = s.FirstNavDate,
        LastNavDate = s.LastNavDate,
        LastNav = s.LastNav,
        IsActive = s.IsActive,
    };
}
