using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services.MarketFactors;

/// <summary>How one daily dataset last went, for the page to show next to its numbers.</summary>
public sealed class MarketFactorsDatasetStatus
{
    public string Dataset { get; init; } = string.Empty;
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateOnly? NewestDay { get; set; }
    public string? LastMessage { get; set; }
    public bool LastFailed { get; set; }
}

/// <summary>Process-wide record of the daily fetches, so a failure is visible rather than silent.</summary>
public sealed class MarketFactorsStatus
{
    private readonly ConcurrentDictionary<string, MarketFactorsDatasetStatus> _datasets = new();

    public MarketFactorsDatasetStatus For(string dataset) =>
        _datasets.GetOrAdd(dataset, key => new MarketFactorsDatasetStatus { Dataset = key });

    public IReadOnlyList<MarketFactorsDatasetStatus> All() => _datasets.Values.OrderBy(x => x.Dataset).ToList();
}

/// <summary>What one sync run did, dataset by dataset.</summary>
public sealed record MarketFactorsSyncReport(IReadOnlyList<string> Lines);

/// <summary>
/// Fetches NSE's end-of-day market factors and keeps them in the database: the
/// participant-wise open interest, the F&amp;O bhavcopy's futures rows, and the
/// FII/DII cash figures.
/// </summary>
/// <remarks>
/// NSE publishes these in the evening (roughly 18:00–20:00 IST). A run looks back
/// over recent NSE sessions and fetches only the days that are missing, so an API
/// that was down for a few evenings catches up by itself. Requests are paced and
/// capped per run: these are NSE's public archives, not an API with a quota, and
/// a burst would be the quickest way to get the server's address refused.
/// </remarks>
public sealed class MarketFactorsSync
{
    public const string HttpClientName = "market-factors";
    public const string ParticipantDataset = "participant-oi";
    public const string FuturesDataset = "futures-bhavcopy";
    public const string CashDataset = "fii-dii-cash";

    public static string ParticipantUrl(DateOnly day) =>
        $"https://archives.nseindia.com/content/nsccl/fao_participant_oi_{day:ddMMyyyy}.csv";

    public static string FuturesBhavcopyUrl(DateOnly day) =>
        $"https://nsearchives.nseindia.com/content/fo/BhavCopy_NSE_FO_0_0_0_{day:yyyyMMdd}_F_0000.csv.zip";

    public const string CashFlowsUrl = "https://www.nseindia.com/api/fiidiiTradeReact";

    private const int MaxFetchesPerDataset = 25;
    /// <summary>The pause before every request to NSE; zero in tests.</summary>
    public TimeSpan Pace { get; set; } = TimeSpan.FromMilliseconds(700);

    private readonly TradingDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IMarketCalendar _calendar;
    private readonly MarketFactorsStatus _status;
    private readonly ILogger<MarketFactorsSync> _logger;

    /// <summary>The clock a run reads "today" from; replaced in tests.</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    public MarketFactorsSync(TradingDbContext db, IHttpClientFactory http, IMarketCalendar calendar,
        MarketFactorsStatus status, ILogger<MarketFactorsSync> logger)
    {
        _db = db;
        _http = http;
        _calendar = calendar;
        _status = status;
        _logger = logger;
    }

    /// <summary>
    /// NSE sessions from newest to oldest, <paramref name="count"/> of them, ending
    /// today once NSE's evening files can exist (18:00 IST) and yesterday before that.
    /// </summary>
    public static IReadOnlyList<DateOnly> RecentSessions(DateTime nowUtc, int count, Func<DateOnly, bool> isHoliday)
    {
        var nowIst = IstTime.ToIst(nowUtc);
        var day = DateOnly.FromDateTime(nowIst);
        if (nowIst.TimeOfDay < new TimeSpan(18, 0, 0)) day = day.AddDays(-1);

        var sessions = new List<DateOnly>();
        for (int guard = 0; sessions.Count < count && guard < count * 3 + 20; guard++, day = day.AddDays(-1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            if (isHoliday(day)) continue;
            sessions.Add(day);
        }

        return sessions;
    }

    public async Task<MarketFactorsSyncReport> RunAsync(int lookbackSessions, CancellationToken ct)
    {
        var lines = new List<string>();
        var sessions = RecentSessions(Clock(), Math.Clamp(lookbackSessions, 1, 250),
            d => _calendar.HolidayOn("NSE", d) is { Closure: MarketClosure.FullDay });

        lines.Add(await SyncParticipantsAsync(sessions, ct));
        lines.Add(await SyncFuturesAsync(sessions.Take(Math.Min(sessions.Count, 15)).ToList(), ct));
        lines.Add(await SyncCashAsync(ct));

        foreach (var line in lines) _logger.LogInformation("Market factors sync: {Line}", line);
        return new MarketFactorsSyncReport(lines);
    }

    private async Task<string> SyncParticipantsAsync(IReadOnlyList<DateOnly> sessions, CancellationToken ct)
    {
        var status = _status.For(ParticipantDataset);
        status.LastAttemptUtc = DateTime.UtcNow;
        var oldest = sessions.Count > 0 ? sessions[^1] : IstTime.DateOf(Clock());
        var have = (await _db.MarketParticipantOpenInterest.AsNoTracking()
                .Where(x => x.Date >= oldest).Select(x => x.Date).Distinct().ToListAsync(ct))
            .ToHashSet();

        int stored = 0, notYet = 0, failed = 0, fetches = 0;
        string? lastError = null;
        foreach (var day in sessions.Where(d => !have.Contains(d)).OrderBy(d => d))
        {
            if (fetches++ >= MaxFetchesPerDataset) break;
            string url = ParticipantUrl(day);
            try
            {
                var (code, body) = await GetTextAsync(url, ct);
                if (code == HttpStatusCode.NotFound) { notYet++; continue; }
                if (code != HttpStatusCode.OK) { failed++; lastError = $"{day:yyyy-MM-dd}: HTTP {(int)code}"; continue; }

                var rows = MarketFactorParsers.ParseParticipantOpenInterest(body, day, url);
                _db.MarketParticipantOpenInterest.RemoveRange(_db.MarketParticipantOpenInterest.Where(x => x.Date == day));
                _db.MarketParticipantOpenInterest.AddRange(rows);
                await _db.SaveChangesAsync(ct);
                stored++;
                status.NewestDay = status.NewestDay is null || day > status.NewestDay ? day : status.NewestDay;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                lastError = $"{day:yyyy-MM-dd}: {ex.Message}";
            }
        }

        return Finish(status, $"participant OI: {stored} day(s) stored, {notYet} not published, {failed} failed", failed, lastError,
            await _db.MarketParticipantOpenInterest.AsNoTracking().MaxAsync(x => (DateOnly?)x.Date, ct));
    }

    private async Task<string> SyncFuturesAsync(IReadOnlyList<DateOnly> sessions, CancellationToken ct)
    {
        var status = _status.For(FuturesDataset);
        status.LastAttemptUtc = DateTime.UtcNow;
        var oldest = sessions.Count > 0 ? sessions[^1] : IstTime.DateOf(Clock());
        var have = (await _db.MarketFuturesDaily.AsNoTracking()
                .Where(x => x.Date >= oldest).Select(x => x.Date).Distinct().ToListAsync(ct))
            .ToHashSet();

        int stored = 0, notYet = 0, failed = 0, fetches = 0;
        string? lastError = null;
        foreach (var day in sessions.Where(d => !have.Contains(d)).OrderBy(d => d))
        {
            if (fetches++ >= MaxFetchesPerDataset) break;
            string url = FuturesBhavcopyUrl(day);
            try
            {
                var client = _http.CreateClient(HttpClientName);
                await Task.Delay(Pace, ct);
                using var response = await client.GetAsync(url, ct);
                if (response.StatusCode == HttpStatusCode.NotFound) { notYet++; continue; }
                if (!response.IsSuccessStatusCode) { failed++; lastError = $"{day:yyyy-MM-dd}: HTTP {(int)response.StatusCode}"; continue; }

                await using var zipStream = await response.Content.ReadAsStreamAsync(ct);
                using var memory = new MemoryStream();
                await zipStream.CopyToAsync(memory, ct);
                memory.Position = 0;
                using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    ?? throw new FormatException("the zip holds no CSV");
                using var reader = new StreamReader(entry.Open());
                var csv = await reader.ReadToEndAsync(ct);

                var rows = MarketFactorParsers.ParseFuturesBhavcopy(csv, url);
                if (rows.Any(r => r.Date != day))
                    throw new FormatException($"the bhavcopy is dated {rows[0].Date:yyyy-MM-dd}, expected {day:yyyy-MM-dd}");

                _db.MarketFuturesDaily.RemoveRange(_db.MarketFuturesDaily.Where(x => x.Date == day));
                _db.MarketFuturesDaily.AddRange(rows);
                await _db.SaveChangesAsync(ct);
                stored++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                lastError = $"{day:yyyy-MM-dd}: {ex.Message}";
            }
        }

        return Finish(status, $"futures bhavcopy: {stored} day(s) stored, {notYet} not published, {failed} failed", failed, lastError,
            await _db.MarketFuturesDaily.AsNoTracking().MaxAsync(x => (DateOnly?)x.Date, ct));
    }

    private async Task<string> SyncCashAsync(CancellationToken ct)
    {
        var status = _status.For(CashDataset);
        status.LastAttemptUtc = DateTime.UtcNow;
        string? error = null;
        int stored = 0;
        try
        {
            var (code, body) = await GetTextAsync(CashFlowsUrl, ct);
            if (code != HttpStatusCode.OK) throw new HttpRequestException($"HTTP {(int)code}");

            foreach (var row in MarketFactorParsers.ParseCashFlows(body, CashFlowsUrl))
            {
                var existing = await _db.MarketCashFlows.FirstOrDefaultAsync(x => x.Date == row.Date && x.Category == row.Category, ct);
                if (existing is null)
                {
                    _db.MarketCashFlows.Add(row);
                    stored++;
                }
                else
                {
                    existing.BuyValueCrore = row.BuyValueCrore;
                    existing.SellValueCrore = row.SellValueCrore;
                    existing.NetValueCrore = row.NetValueCrore;
                    existing.FetchedUtc = row.FetchedUtc;
                }
            }

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
        }

        return Finish(status, error is null ? $"FII/DII cash: {stored} new row(s)" : "FII/DII cash: failed", error is null ? 0 : 1, error,
            await _db.MarketCashFlows.AsNoTracking().MaxAsync(x => (DateOnly?)x.Date, ct));
    }

    private static string Finish(MarketFactorsDatasetStatus status, string line, int failed, string? error, DateOnly? newest)
    {
        status.NewestDay = newest;
        status.LastFailed = failed > 0;
        status.LastMessage = error is null ? line : $"{line} — last error: {error}";
        if (failed == 0) status.LastSuccessUtc = DateTime.UtcNow;
        return status.LastMessage;
    }

    private async Task<(HttpStatusCode Code, string Body)> GetTextAsync(string url, CancellationToken ct)
    {
        var client = _http.CreateClient(HttpClientName);
        await Task.Delay(Pace, ct);
        using var response = await client.GetAsync(url, ct);
        string body = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : string.Empty;
        return (response.StatusCode, body);
    }

    /// <summary>The headers NSE's archives answer to: a browser-like agent and no compression surprises.</summary>
    public static void Configure(HttpClient client)
    {
        client.Timeout = TimeSpan.FromSeconds(40);
        // The plain agent the archives were checked with from the server (17 Sep 2026); NSE's edge refuses some others.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

}
