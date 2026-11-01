using System.Globalization;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MutualFunds;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>The reads behind api/MutualFunds: coverage first, then schemes and their NAVs.</summary>
public sealed class MutualFundQueries
{
    /// <summary>Below this many rows an exact count is cheap; above it TimescaleDB's estimate is used.</summary>
    public const long ExactCountBelow = 2_000_000;

    public const int MaxTake = 200;

    private readonly TradingDbContext _db;
    private readonly IMarketCalendar _calendar;
    private readonly IProcessSettingsStore _settings;
    private readonly MutualFundStatus _status;
    private readonly MutualFundsOptions _options;
    private readonly IHttpClientFactory _http;

    public MutualFundQueries(TradingDbContext db, IMarketCalendar calendar, IProcessSettingsStore settings, MutualFundStatus status,
        IOptions<MutualFundsOptions> options, IHttpClientFactory http)
    {
        _db = db;
        _calendar = calendar;
        _settings = settings;
        _status = status;
        _options = options.Value;
        _http = http;
    }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public async Task<MutualFundCoverageResponse> CoverageAsync(CancellationToken ct)
    {
        var now = Clock();
        var schemes = _db.MutualFundSchemes.AsNoTracking();

        var response = new MutualFundCoverageResponse
        {
            ServerUtc = now,
            Schemes = await schemes.CountAsync(ct),
            ActiveSchemes = await schemes.CountAsync(s => s.IsActive, ct),
            ByPlan = await CountsAsync(schemes.GroupBy(s => s.Plan).Select(g => new MutualFundCountDto { Key = g.Key, Schemes = g.Count(), Active = g.Count(s => s.IsActive) }), ct),
            ByOption = await CountsAsync(schemes.GroupBy(s => s.Option).Select(g => new MutualFundCountDto { Key = g.Key, Schemes = g.Count(), Active = g.Count(s => s.IsActive) }), ct),
            ByCategory = await CountsAsync(schemes.GroupBy(s => s.Category).Select(g => new MutualFundCountDto { Key = g.Key, Schemes = g.Count(), Active = g.Count(s => s.IsActive) }), ct),
            ByAmc = await CountsAsync(schemes.GroupBy(s => s.Amc).Select(g => new MutualFundCountDto { Key = g.Key, Schemes = g.Count(), Active = g.Count(s => s.IsActive) }), ct),
            SyncEnabled = _options.SyncEnabled,
            SyncRunning = _status.SyncRunning,
        };

        var navs = _db.MutualFundNavs.AsNoTracking();
        var (rows, approximate) = await NavRowCountAsync(ct);
        var latestFull = await MutualFundNavSync.LatestFullDateAsync(_db, ct);
        var expected = MutualFundSchedule.ExpectedNavDate(now, IsBusinessDay);
        response.Nav = new MutualFundNavCoverageDto
        {
            Rows = rows,
            RowsApproximate = approximate,
            FirstDate = await navs.MinAsync(x => (DateOnly?)x.Date, ct),
            LastDate = await navs.MaxAsync(x => (DateOnly?)x.Date, ct),
            LatestFullDate = latestFull,
            ExpectedDate = expected,
            BusinessDaysBehind = latestFull is null ? null : MutualFundSchedule.BusinessDaysBehind(latestFull, expected, IsBusinessDay),
        };

        if (await MutualFundSettings.LastSyncAsync(_settings, ct) is { } last)
        {
            response.LastSync = new MutualFundSyncDto
            {
                Trigger = last.Trigger,
                StartedUtc = last.StartedUtc,
                FinishedUtc = last.FinishedUtc,
                Ok = last.Ok,
                Message = last.Message,
                AsOfDate = last.AsOfDate,
                Rows = last.Rows,
                NewSchemes = last.NewSchemes,
                NavRowsWritten = last.NavRowsWritten,
            };
        }

        response.Backfill = await BackfillAsync(ct);
        return response;
    }

    public async Task<MutualFundBackfillDto> BackfillAsync(CancellationToken ct)
    {
        var state = await MutualFundSettings.BackfillAsync(_settings, ct);
        return new MutualFundBackfillDto
        {
            Enabled = _options.BackfillEnabled,
            Requested = state.Requested,
            Running = _status.BackfillRunning,
            State = state.State,
            Now = _status.BackfillNow,
            From = state.From,
            Top = state.Top,
            Cursor = state.Cursor,
            WindowsDone = state.WindowsDone,
            WindowsLeft = state.WindowsLeft(),
            RowsWritten = state.RowsWritten,
            MaxWindows = state.MaxWindows,
            LastWindow = state.LastWindow,
            LastError = state.LastError,
            StartedUtc = state.StartedUtc,
            UpdatedUtc = state.UpdatedUtc,
        };
    }

    /// <summary>
    /// Schemes whose name, AMC or ISIN contains <paramref name="search"/>, or whose
    /// code is it; active ones first, then by name.
    /// </summary>
    public async Task<List<MutualFundSchemeDto>> SchemesAsync(string? search, string? plan, string? option, bool? active, int take, CancellationToken ct)
    {
        var query = _db.MutualFundSchemes.AsNoTracking();
        string term = (search ?? string.Empty).Trim();
        if (term.Length > 0)
        {
            string lower = term.ToLowerInvariant();
            bool isCode = int.TryParse(term, NumberStyles.None, CultureInfo.InvariantCulture, out int code);
            query = query.Where(s =>
                (isCode && s.SchemeCode == code)
                || s.SchemeName.ToLower().Contains(lower)
                || s.Amc.ToLower().Contains(lower)
                || (s.IsinGrowth != null && s.IsinGrowth.ToLower() == lower)
                || (s.IsinReinvest != null && s.IsinReinvest.ToLower() == lower));
        }

        string planKey = (plan ?? string.Empty).Trim().ToLowerInvariant();
        string optionKey = (option ?? string.Empty).Trim().ToLowerInvariant();
        if (planKey.Length > 0) query = query.Where(s => s.Plan == planKey);
        if (optionKey.Length > 0) query = query.Where(s => s.Option == optionKey);
        if (active is { } isActive) query = query.Where(s => s.IsActive == isActive);

        return await query
            .OrderByDescending(s => s.IsActive)
            .ThenBy(s => s.SchemeName)
            .ThenBy(s => s.SchemeCode)
            .Take(Math.Clamp(take, 1, MaxTake))
            .Select(s => ToDto(s))
            .ToListAsync(ct);
    }

    public async Task<MutualFundSchemeDto?> SchemeAsync(int code, CancellationToken ct) =>
        await _db.MutualFundSchemes.AsNoTracking().Where(s => s.SchemeCode == code).Select(s => ToDto(s)).FirstOrDefaultAsync(ct);

    /// <summary>One scheme's NAVs between two days (either may be open), oldest first.</summary>
    public async Task<MutualFundNavResponse?> NavAsync(int code, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (await SchemeAsync(code, ct) is not { } scheme) return null;

        var query = _db.MutualFundNavs.AsNoTracking().Where(n => n.SchemeCode == code);
        if (from is { } start) query = query.Where(n => n.Date >= start);
        if (to is { } end) query = query.Where(n => n.Date <= end);

        return new MutualFundNavResponse
        {
            Scheme = scheme,
            From = from,
            To = to,
            Rows = await query.OrderBy(n => n.Date).Select(n => new MutualFundNavPointDto { Date = n.Date, Nav = n.Nav }).ToListAsync(ct),
        };
    }

    /// <summary>
    /// Compares one scheme's stored NAVs with mfapi.in's copy of AMFI's history.
    /// A check, not a source: nothing it reads is stored.
    /// </summary>
    public async Task<MutualFundSpotCheckResponse> SpotCheckAsync(int code, CancellationToken ct)
    {
        string url = _options.SpotCheckUrl + code.ToString(CultureInfo.InvariantCulture);
        using var response = await _http.CreateClient(MutualFundsOptions.HttpClientName).GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var theirs = ParseMfApi(await response.Content.ReadAsStringAsync(ct));

        var ours = await _db.MutualFundNavs.AsNoTracking().Where(n => n.SchemeCode == code)
            .Select(n => new { n.Date, n.Nav }).ToListAsync(ct);
        return Compare(code, url, ours.ToDictionary(n => n.Date, n => n.Nav), theirs);
    }

    /// <summary>mfapi.in's <c>{"data":[{"date":"30-10-2026","nav":"162.37510"}]}</c>, by day.</summary>
    public static Dictionary<DateOnly, decimal> ParseMfApi(string json)
    {
        var result = new Dictionary<DateOnly, decimal>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return result;
        foreach (var point in data.EnumerateArray())
        {
            if (point.TryGetProperty("date", out var date) && point.TryGetProperty("nav", out var nav)
                && DateOnly.TryParseExact(date.GetString(), "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && decimal.TryParse(nav.GetString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
                && value > 0)
            {
                result[day] = value;
            }
        }

        return result;
    }

    /// <summary>Day by day within our stored range; values are compared as numbers ("162.37510" equals "162.3751").</summary>
    public static MutualFundSpotCheckResponse Compare(int code, string source, IReadOnlyDictionary<DateOnly, decimal> ours, IReadOnlyDictionary<DateOnly, decimal> theirs)
    {
        var result = new MutualFundSpotCheckResponse { SchemeCode = code, Source = source };
        if (ours.Count == 0) return result;

        var first = ours.Keys.Min();
        var last = ours.Keys.Max();
        foreach (var (day, value) in ours.OrderBy(p => p.Key))
        {
            if (!theirs.TryGetValue(day, out var other))
            {
                result.MissingThere++;
                continue;
            }

            result.Compared++;
            if (value == other)
            {
                result.Matching++;
            }
            else
            {
                result.DifferentCount++;
                if (result.Different.Count < 20) result.Different.Add(new MutualFundSpotCheckDiffDto { Date = day, Ours = value, Theirs = other });
            }
        }

        foreach (var day in theirs.Keys.Where(d => d >= first && d <= last && !ours.ContainsKey(d)).OrderBy(d => d))
        {
            result.MissingHere++;
            if (result.MissingHereSample.Count < 20) result.MissingHereSample.Add(day);
        }

        return result;
    }

    private bool IsBusinessDay(DateOnly day) => MutualFundNavSync.IsBusinessDay(_calendar, day);

    private async Task<(long Rows, bool Approximate)> NavRowCountAsync(CancellationToken ct)
    {
        if (_db.Database.IsNpgsql())
        {
            // count(*) over tens of millions of compressed rows takes seconds; the
            // estimate is free and good to a fraction of a percent after ANALYZE.
            long estimate = await _db.Database.SqlQueryRaw<long>("SELECT approximate_row_count('mf_nav') AS \"Value\"").SingleAsync(ct);
            if (estimate >= ExactCountBelow) return (estimate, true);
        }

        return (await _db.MutualFundNavs.LongCountAsync(ct), false);
    }

    private static async Task<List<MutualFundCountDto>> CountsAsync(IQueryable<MutualFundCountDto> query, CancellationToken ct) =>
        (await query.ToListAsync(ct)).OrderByDescending(c => c.Schemes).ThenBy(c => c.Key).ToList();

    private static MutualFundSchemeDto ToDto(MutualFundScheme s) => new()
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
