using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>
/// Downloads AMFI's daily NAV file (<c>NAVAll.txt</c>, every scheme's latest
/// NAV, about 1.5 MB) and keeps <c>mf_schemes</c> and <c>mf_nav</c> current.
/// </summary>
/// <remarks>
/// Idempotent: a NAV already stored with the same value is not written again,
/// and the master is merged, never replaced. Dormant schemes stay in AMFI's
/// file with their last NAV for years (some from 2018); they are written once.
/// The NAVs, the master and the active flags are written in one transaction,
/// so a sync that fails half-way leaves the tables as they were.
/// </remarks>
public sealed class MutualFundNavSync
{
    /// <summary>Fewer schemes than this is a broken or partial file, not AMFI's (14,441 on 1 Nov 2026).</summary>
    public const int MinimumRows = 5000;

    private readonly TradingDbContext _db;
    private readonly MutualFundStore _store;
    private readonly IHttpClientFactory _http;
    private readonly IMarketCalendar _calendar;
    private readonly IProcessSettingsStore _settings;
    private readonly MutualFundStatus _status;
    private readonly MutualFundsOptions _options;
    private readonly ILogger<MutualFundNavSync> _logger;

    public MutualFundNavSync(TradingDbContext db, MutualFundStore store, IHttpClientFactory http, IMarketCalendar calendar,
        IProcessSettingsStore settings, MutualFundStatus status, IOptions<MutualFundsOptions> options, ILogger<MutualFundNavSync> logger)
    {
        _db = db;
        _store = store;
        _http = http;
        _calendar = calendar;
        _settings = settings;
        _status = status;
        _options = options.Value;
        _logger = logger;
    }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>A weekday the NSE calendar does not close: the business-day rule for AMFI's NAVs.</summary>
    public static bool IsBusinessDay(IMarketCalendar calendar, DateOnly day) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
        && calendar.HolidayOn("NSE", day) is not { Closure: MarketClosure.FullDay };

    public bool IsBusinessDay(DateOnly day) => IsBusinessDay(_calendar, day);

    public Task<MutualFundSyncResult?> LastResultAsync(CancellationToken ct) => MutualFundSettings.LastSyncAsync(_settings, ct);

    /// <summary>Fetches today's file and stores it. Never throws for AMFI's or the database's faults: the result says what happened.</summary>
    public async Task<MutualFundSyncResult> RunAsync(string trigger, CancellationToken ct)
    {
        var started = Clock();
        var expected = MutualFundSchedule.ExpectedNavDate(started, IsBusinessDay);
        MutualFundSyncResult result;

        await _status.WriteGate.WaitAsync(ct);
        _status.SyncRunning = true;
        try
        {
            var counts = new AmfiParseCounts();
            List<AmfiNavRow> rows;
            using (var response = await _http.CreateClient(MutualFundsOptions.HttpClientName)
                       .GetAsync(_options.DailyUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                rows = AmfiNavParser.Parse(reader, counts).ToList();
            }

            if (rows.Count < MinimumRows)
                throw new FormatException($"AMFI's file had {rows.Count:N0} schemes, fewer than the {MinimumRows:N0} expected; nothing was stored.");

            var asOf = AsOfDate(rows);
            var stored = await _store.LastNavsAsync(ct);
            var navs = NavsToWrite(rows, stored, MutualFundNavSources.AmfiDaily);
            var schemes = rows.Select(ToSchemeUpsert).ToList();
            int newSchemes = rows.Count(r => !stored.ContainsKey(r.SchemeCode));

            int written;
            await using (var tx = await _db.Database.BeginTransactionAsync(ct))
            {
                // NAVs before the master: the master's last NAV day decides what the
                // next sync skips, so it must never get ahead of the NAVs it describes.
                written = await _store.UpsertNavsAsync(navs, ct);
                await _store.UpsertSchemesAsync(schemes, authoritative: true, started, ct);
                if (asOf is not null) await _store.RefreshActiveAsync(asOf.Value, MutualFundSchedule.ActiveWithinDays, ct);
                await tx.CommitAsync(ct);
            }

            string reached = asOf is null ? "no NAV day" : asOf >= expected ? $"NAVs for {asOf:dd MMM yyyy}" : $"NAVs for {asOf:dd MMM yyyy}, still short of {expected:dd MMM yyyy}";
            result = new MutualFundSyncResult
            {
                Trigger = trigger,
                StartedUtc = started,
                FinishedUtc = Clock(),
                Ok = true,
                AsOfDate = asOf,
                ExpectedDate = expected,
                Rows = rows.Count,
                RowsWithoutNav = counts.RowsWithoutNav,
                NewSchemes = newSchemes,
                NavRowsWritten = written,
                Message = $"{rows.Count:N0} schemes read ({newSchemes:N0} new, {counts.RowsWithoutNav:N0} without a usable NAV); "
                          + $"{written:N0} NAVs written; {reached}.",
            };
            _logger.LogInformation("Mutual fund NAV sync ({Trigger}): {Message}", trigger, result.Message);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _db.ChangeTracker.Clear();
            result = new MutualFundSyncResult
            {
                Trigger = trigger,
                StartedUtc = started,
                FinishedUtc = Clock(),
                Ok = false,
                ExpectedDate = expected,
                Message = $"Failed: {ex.Message}",
            };
            _logger.LogWarning(ex, "Mutual fund NAV sync ({Trigger}) failed: {Error}", trigger, ex.Message);
        }
        finally
        {
            _status.SyncRunning = false;
            _status.WriteGate.Release();
        }

        await MutualFundSettings.SaveSyncAsync(_settings, result, ct);
        return result;
    }

    /// <summary>The NAV day most rows carry: the business day the file is for. Liquid funds' weekend NAVs never outvote it.</summary>
    public static DateOnly? AsOfDate(IEnumerable<AmfiNavRow> rows) =>
        rows.Where(r => r is { Nav: not null, Date: not null })
            .GroupBy(r => r.Date!.Value)
            .OrderByDescending(g => g.Count())
            .ThenByDescending(g => g.Key)
            .Select(g => (DateOnly?)g.Key)
            .FirstOrDefault();

    /// <summary>
    /// The NAVs worth writing: a day newer than the scheme's stored last NAV
    /// day, or the same day with a different value (AMFI's correction). A
    /// dormant scheme's years-old NAV is written the first time only.
    /// </summary>
    public static List<MutualFundNav> NavsToWrite(IEnumerable<AmfiNavRow> rows, IReadOnlyDictionary<int, (DateOnly? Date, decimal? Nav)> stored, string source)
    {
        var result = new List<MutualFundNav>();
        foreach (var row in rows)
        {
            if (row.Nav is not { } nav || row.Date is not { } date) continue;
            if (stored.TryGetValue(row.SchemeCode, out var last) && last.Date is { } lastDate)
            {
                if (date < lastDate) continue;
                if (date == lastDate && last.Nav == nav) continue;
            }

            result.Add(new MutualFundNav { SchemeCode = row.SchemeCode, Date = date, Nav = nav, Source = source });
        }

        return result;
    }

    /// <summary>What one row says about its scheme.</summary>
    public static SchemeUpsert ToSchemeUpsert(AmfiNavRow row)
    {
        bool hasNav = row is { Nav: not null, Date: not null };
        return new SchemeUpsert(row.SchemeCode, row.IsinGrowth, row.IsinReinvest, row.Amc, row.SchemeName, row.Plan, row.Option,
            row.OptionLabel, row.Category, hasNav ? row.Date : null, hasNav ? row.Date : null, hasNav ? row.Nav : null);
    }

    /// <summary>The coverage page's freshness: the newest full NAV day and the business day expected by now.</summary>
    public async Task<(DateOnly Expected, DateOnly? LatestFull, int BusinessDaysBehind)> FreshnessAsync(CancellationToken ct)
    {
        var expected = MutualFundSchedule.ExpectedNavDate(Clock(), IsBusinessDay);
        var latestFull = await LatestFullDateAsync(_db, ct);
        return (expected, latestFull, MutualFundSchedule.BusinessDaysBehind(latestFull, expected, IsBusinessDay));
    }

    /// <summary>
    /// The newest day on which at least half as many schemes have a NAV as on
    /// the busiest day of the fortnight before it. A Sunday with only the liquid
    /// funds' NAVs is not a full day; so the check never takes 712 rows for a
    /// fresh file.
    /// </summary>
    public static async Task<DateOnly?> LatestFullDateAsync(TradingDbContext db, CancellationToken ct)
    {
        var newest = await db.MutualFundNavs.AsNoTracking().MaxAsync(x => (DateOnly?)x.Date, ct);
        if (newest is null) return null;

        var since = newest.Value.AddDays(-14);
        var perDay = await db.MutualFundNavs.AsNoTracking()
            .Where(x => x.Date >= since)
            .GroupBy(x => x.Date)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return LatestFullDate(perDay.Select(d => (d.Date, d.Count)));
    }

    public static DateOnly? LatestFullDate(IEnumerable<(DateOnly Date, int Count)> perDay)
    {
        var days = perDay.ToList();
        if (days.Count == 0) return null;
        int peak = days.Max(d => d.Count);
        return days.Where(d => d.Count * 2 >= peak).Max(d => d.Date);
    }
}
