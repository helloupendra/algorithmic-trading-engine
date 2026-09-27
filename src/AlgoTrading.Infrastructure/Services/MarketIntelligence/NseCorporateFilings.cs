using System.Globalization;
using System.Net;
using System.Text.Json;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>
/// Reads NSE's corporate-announcements and event-calendar answers. Pure
/// functions over text, tested against real answers of 27 Sep 2026.
/// </summary>
public static class NseCorporateParsers
{
    private static readonly string[] IstStampFormats = ["dd-MMM-yyyy HH:mm:ss", "d-MMM-yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss"];

    /// <summary>
    /// The announcements in one answer of <c>/api/corporate-announcements</c>,
    /// each keyed by NSE's own id for it (<c>seq_id</c>).
    /// </summary>
    /// <remarks>
    /// <c>desc</c> is NSE's subject line ("Outcome of Board Meeting");
    /// <c>attchmntText</c> its summary of the filing; <c>exchdisstime</c> the
    /// broadcast time (IST), with <c>an_dt</c> and <c>sort_date</c> as
    /// fallbacks. An item without a symbol is skipped: there is nothing to
    /// attach it to.
    /// </remarks>
    public static IReadOnlyList<CorporateAnnouncement> ParseAnnouncements(string json, DateTime firstSeenUtc)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("corporate announcements answer is not a list");

        var rows = new List<CorporateAnnouncement>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            string symbol = Text(item, "symbol");
            if (symbol.Length == 0) continue;

            var announced = IstStamp(Text(item, "exchdisstime")) ?? IstStamp(Text(item, "an_dt")) ?? IstStamp(Text(item, "sort_date"));
            string subject = Text(item, "desc");
            string attachment = Text(item, "attchmntFile");
            string seq = Text(item, "seq_id");

            // NSE's id when it gives one; otherwise what makes a filing itself.
            string identity = seq.Length > 0
                ? $"NSE|seq|{seq}"
                : $"NSE|{symbol}|{announced:O}|{subject}|{attachment}";

            rows.Add(new CorporateAnnouncement
            {
                Exchange = "NSE",
                Symbol = symbol,
                Company = Text(item, "sm_name"),
                Subject = subject,
                Details = Text(item, "attchmntText"),
                AttachmentUrl = attachment,
                AnnouncedUtc = announced,
                FirstSeenUtc = firstSeenUtc,
                UniqueKey = RssFeedParser.Sha256Hex(identity),
            });
        }

        return rows;
    }

    /// <summary>The board meetings in one answer of <c>/api/event-calendar</c>.</summary>
    public static IReadOnlyList<CorporateCalendarEvent> ParseEventCalendar(string json, DateTime firstSeenUtc)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("event calendar answer is not a list");

        var rows = new List<CorporateCalendarEvent>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            string symbol = Text(item, "symbol");
            string dateText = Text(item, "date");
            if (symbol.Length == 0) continue;
            if (!DateOnly.TryParseExact(dateText, ["dd-MMM-yyyy", "d-MMM-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new FormatException($"event calendar date '{dateText}' does not parse");

            string purpose = Text(item, "purpose");
            rows.Add(new CorporateCalendarEvent
            {
                Exchange = "NSE",
                Symbol = symbol,
                Company = Text(item, "company"),
                Purpose = purpose,
                EventDate = date,
                FirstSeenUtc = firstSeenUtc,
                UniqueKey = RssFeedParser.Sha256Hex($"NSE|{symbol}|{date:yyyy-MM-dd}|{purpose}"),
            });
        }

        return rows;
    }

    /// <summary>An NSE stamp, which is IST wall-clock time, as UTC; null when absent or unreadable.</summary>
    public static DateTime? IstStamp(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTime.TryParseExact(text.Trim(), IstStampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ist)
            ? DateTime.SpecifyKind(ist - IstTime.Offset, DateTimeKind.Utc)
            : null;
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : string.Empty;
}

/// <summary>What one filings poll did.</summary>
public sealed record FilingsRecordReport(int Read, int Stored, bool AskedForWholeDays);

/// <summary>
/// Stores NSE's corporate announcements and board-meeting calendar, insert-only,
/// each row with the moment the desk first saw it.
/// </summary>
/// <remarks>
/// <para>
/// NSE's announcements answer holds the latest 20 filings, and a busy results
/// evening files more than 20 in ten minutes. So when none of the 20 is one
/// already stored, or on the first poll after the API starts, the recorder
/// also asks for the whole of the days since the newest stored filing (at most
/// <see cref="CatchUpDays"/> back), by date, which NSE answers in full. That
/// is one request of about a megabyte after a restart or a burst, and the
/// small one otherwise.
/// </para>
/// <para>
/// A filing fetched that way carries its real broadcast time in AnnouncedUtc
/// and the fetch time in FirstSeenUtc; see <see cref="CorporateAnnouncement"/>.
/// </para>
/// </remarks>
public sealed class CorporateFilingsRecorder
{
    public const string AnnouncementsPage = "https://www.nseindia.com/companies-listing/corporate-filings-announcements";
    public const string CalendarPage = "https://www.nseindia.com/companies-listing/corporate-filings-event-calendar";
    public const string LatestAnnouncementsUrl = "https://www.nseindia.com/api/corporate-announcements?index=equities";

    /// <summary>How far back a catch-up asks, at most: a long weekend's worth.</summary>
    public const int CatchUpDays = 3;

    /// <summary>How far ahead the calendar is read.</summary>
    public const int CalendarDaysAhead = 90;

    private const string AnnouncementsSource = "NSE announcements";
    private const string CalendarSource = "NSE event calendar";

    private readonly TradingDbContext _db;
    private readonly NseWebClient _nse;
    private readonly MarketIntelligenceStatus _status;
    private readonly ILogger<CorporateFilingsRecorder> _logger;

    public CorporateFilingsRecorder(TradingDbContext db, NseWebClient nse, MarketIntelligenceStatus status, ILogger<CorporateFilingsRecorder> logger)
    {
        _db = db;
        _nse = nse;
        _status = status;
        _logger = logger;
    }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public static string AnnouncementsForDaysUrl(DateOnly from, DateOnly to) =>
        $"https://www.nseindia.com/api/corporate-announcements?index=equities&from_date={from:dd-MM-yyyy}&to_date={to:dd-MM-yyyy}";

    public static string CalendarUrl(DateOnly from, DateOnly to) =>
        $"https://www.nseindia.com/api/event-calendar?index=equities&from_date={from:dd-MM-yyyy}&to_date={to:dd-MM-yyyy}";

    /// <summary>
    /// One announcements poll. <paramref name="firstSinceStart"/> makes it ask
    /// for the missed days too, as after a restart.
    /// </summary>
    public async Task<FilingsRecordReport> RecordAnnouncementsAsync(bool firstSinceStart, CancellationToken ct)
    {
        var health = _status.Recorder(MarketIntelligenceNames.Announcements);
        var now = Clock();
        health.Attempted(now);
        try
        {
            var (status, body) = await _nse.GetJsonAsync(LatestAnnouncementsUrl, AnnouncementsPage, ct);
            if (status != HttpStatusCode.OK) throw new HttpRequestException($"HTTP {(int)status}");
            var latest = NseCorporateParsers.ParseAnnouncements(body, now);

            var keys = latest.Select(a => a.UniqueKey).ToList();
            int known = await _db.CorporateAnnouncements.AsNoTracking().CountAsync(a => keys.Contains(a.UniqueKey), ct);

            // Every one of the latest 20 is new: more may have been filed since the last poll than the answer holds.
            bool gapPossible = latest.Count > 0 && known == 0;
            var rows = latest.ToList();
            if (firstSinceStart || gapPossible)
            {
                var today = IstTime.DateOf(now);
                var newest = await _db.CorporateAnnouncements.AsNoTracking().MaxAsync(a => a.AnnouncedUtc, ct);
                var from = newest is null ? today.AddDays(-1) : IstTime.DateOf(newest.Value);
                if (from < today.AddDays(-CatchUpDays)) from = today.AddDays(-CatchUpDays);

                _logger.LogInformation("Filings: asking NSE for every filing of {From}..{To} ({Reason}).",
                    from, today, firstSinceStart ? "first poll since the API started" : "all of the latest 20 were new");
                var (rangeStatus, rangeBody) = await _nse.GetJsonAsync(AnnouncementsForDaysUrl(from, today), AnnouncementsPage, ct);
                if (rangeStatus != HttpStatusCode.OK) throw new HttpRequestException($"HTTP {(int)rangeStatus} for {from:yyyy-MM-dd}..{today:yyyy-MM-dd}");
                rows.AddRange(NseCorporateParsers.ParseAnnouncements(rangeBody, now));
            }

            // Stamped once the answers are in, never before: a filing broadcast
            // while the request was in flight must not read as seen before it existed.
            var seenUtc = Clock();
            foreach (var row in rows) row.FirstSeenUtc = seenUtc;

            int stored = await InsertNewAsync(_db.CorporateAnnouncements, rows, a => a.UniqueKey, ct);
            var report = new FilingsRecordReport(rows.Count, stored, firstSinceStart || gapPossible);
            health.Finished(Clock(), $"{stored} new filing(s) of {rows.Count} read" + (report.AskedForWholeDays ? ", whole days asked for" : string.Empty));
            health.SourceRecovered(AnnouncementsSource);
            return report;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recorded for the status endpoint and rethrown: the caller logs it,
            // once per condition, and knows the missed days are still owed.
            health.SourceFailed(AnnouncementsSource, ex.Message, Clock());
            health.Finished(Clock(), "failed", ex.Message);
            throw;
        }
    }

    /// <summary>One calendar poll: board meetings from today to <see cref="CalendarDaysAhead"/> days ahead.</summary>
    public async Task<FilingsRecordReport> RecordCalendarAsync(CancellationToken ct)
    {
        var health = _status.Recorder(MarketIntelligenceNames.Calendar);
        var now = Clock();
        health.Attempted(now);
        try
        {
            var today = IstTime.DateOf(now);
            var (status, body) = await _nse.GetJsonAsync(CalendarUrl(today, today.AddDays(CalendarDaysAhead)), CalendarPage, ct);
            if (status != HttpStatusCode.OK) throw new HttpRequestException($"HTTP {(int)status}");

            var rows = NseCorporateParsers.ParseEventCalendar(body, Clock());
            int stored = await InsertNewAsync(_db.CorporateCalendar, rows, e => e.UniqueKey, ct);
            health.Finished(Clock(), $"{stored} new meeting(s) of {rows.Count} listed");
            health.SourceRecovered(CalendarSource);
            return new FilingsRecordReport(rows.Count, stored, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            health.SourceFailed(CalendarSource, ex.Message, Clock());
            health.Finished(Clock(), "failed", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Adds the rows whose key is not stored yet, and never touches one that is:
    /// a stored row's FirstSeenUtc is the first sighting and stays so.
    /// </summary>
    private async Task<int> InsertNewAsync<T>(DbSet<T> set, IReadOnlyList<T> rows, Func<T, string> key, CancellationToken ct) where T : class
    {
        var distinct = rows.DistinctBy(key).ToList();
        if (distinct.Count == 0) return 0;

        var keys = distinct.Select(key).ToList();
        var known = (await set.AsNoTracking()
                .Where(x => keys.Contains(EF.Property<string>(x, "UniqueKey")))
                .Select(x => EF.Property<string>(x, "UniqueKey"))
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var fresh = distinct.Where(r => !known.Contains(key(r))).ToList();
        if (fresh.Count == 0) return 0;

        set.AddRange(fresh);
        try
        {
            await _db.SaveChangesAsync(ct);
            return fresh.Count;
        }
        catch (DbUpdateException)
        {
            // A concurrent poll stored some of them first; its rows stand.
            _db.ChangeTracker.Clear();
            int stored = 0;
            foreach (var row in fresh)
            {
                set.Add(row);
                try
                {
                    await _db.SaveChangesAsync(ct);
                    stored++;
                }
                catch (DbUpdateException)
                {
                }
                finally
                {
                    _db.ChangeTracker.Clear();
                }
            }

            return stored;
        }
    }
}
