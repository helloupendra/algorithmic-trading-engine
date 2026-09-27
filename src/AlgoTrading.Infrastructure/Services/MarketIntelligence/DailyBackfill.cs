using System.Globalization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>How fetching one day of a dataset went.</summary>
public enum DayFetchResult
{
    Stored,

    /// <summary>The source has no file for the day: not yet, or never (a holiday).</summary>
    NotPublished,

    Failed,
}

public sealed record DayFetchOutcome(DayFetchResult Result, string? Error)
{
    public static readonly DayFetchOutcome Stored = new(DayFetchResult.Stored, null);
    public static readonly DayFetchOutcome NotPublished = new(DayFetchResult.NotPublished, null);
    public static DayFetchOutcome Failed(string error) => new(DayFetchResult.Failed, error);
}

/// <summary>A dataset kept one row-set per NSE session, and how to read and fetch it.</summary>
public sealed record DailyDataset(
    string Name,
    DateOnly Earliest,
    Func<DateOnly, DateOnly, CancellationToken, Task<IReadOnlySet<DateOnly>>> DatesPresentAsync,
    Func<DateOnly, CancellationToken, Task<DayFetchOutcome>> FetchDayAsync);

/// <summary>What one backfill run did.</summary>
public sealed record BackfillReport(string Dataset, int Sessions, int ToFetch, int Stored, int NotPublished, int Failed, bool Paused, string? LastError)
{
    public string Describe() =>
        $"{Dataset}: {Stored} day(s) stored, {NotPublished} with no file, {Failed} failed, {Math.Max(0, ToFetch - Stored - NotPublished - Failed)} left"
        + (Paused ? " (paused)" : string.Empty)
        + (LastError is null ? string.Empty : $"; last error: {LastError}");
}

/// <summary>Where a dataset's history stands: what is stored, and what is still missing.</summary>
public sealed record BackfillCoverage(string Dataset, DateOnly? FirstDate, DateOnly? LastDate, int Days, int MissingSessions, int NoFileDays);

/// <summary>
/// Fills a daily dataset's history, one NSE session at a time, newest first,
/// skipping what is already stored. Resumable by construction: a run that is
/// stopped, paused or killed leaves nothing half-done, and the next one starts
/// by asking which days are still missing.
/// </summary>
/// <remarks>
/// <para>
/// The sessions are NSE weekdays that are not full-day holidays in the
/// exchange calendar, plus any weekend special session it lists. The calendar
/// holds recent years only, so older holidays are learnt the hard way: a day
/// that has no file (404) once it is <see cref="NoFileAfterDays"/> days old is
/// remembered in <c>system_settings</c> (<see cref="NoFileKey"/>) and never
/// asked for again, and it does not count as missing. A newer day with no
/// file is simply not published yet and is asked again next time.
/// </para>
/// <para>
/// The pace is the fetcher's (<see cref="NseRequestPacer"/>); the runner asks
/// <c>mayContinue</c> before every day, which is how a run stops at 09:00 on
/// a trading day and carries on after 15:40.
/// </para>
/// </remarks>
public sealed class DailyBackfillRunner
{
    /// <summary>A day this old with no file will never have one.</summary>
    public const int NoFileAfterDays = 3;

    // system_settings.Value is varchar(2000): about 220 dates of 9 characters.
    private const int MaxNoFileChars = 2000;

    private readonly IProcessSettingsStore _settings;
    private readonly IMarketCalendar _calendar;
    private readonly MarketIntelligenceStatus _status;
    private readonly ILogger<DailyBackfillRunner> _logger;

    public DailyBackfillRunner(IProcessSettingsStore settings, IMarketCalendar calendar, MarketIntelligenceStatus status, ILogger<DailyBackfillRunner> logger)
    {
        _settings = settings;
        _calendar = calendar;
        _status = status;
        _logger = logger;
    }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The <c>system_settings</c> key holding a dataset's days known to have no file.</summary>
    public static string NoFileKey(string dataset) => SystemSettingKeys.MarketIntelligenceNoFile(dataset);

    /// <summary>The NSE sessions from <paramref name="from"/> to <paramref name="to"/>, as far as the calendar knows them.</summary>
    public static IReadOnlyList<DateOnly> Sessions(DateOnly from, DateOnly to, IMarketCalendar calendar)
    {
        var days = new List<DateOnly>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            bool weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            if (weekend ? calendar.SpecialSessionOn("NSE", day) is not null : calendar.HolidayOn("NSE", day) is not { Closure: MarketClosure.FullDay })
                days.Add(day);
        }

        return days;
    }

    /// <summary>Fetches the sessions between two dates that are neither stored nor known to have no file, newest first.</summary>
    /// <param name="dataset">What to fill.</param>
    /// <param name="from">The first date wanted.</param>
    /// <param name="to">The last date wanted.</param>
    /// <param name="mayContinue">Asked before every day; false stops the run where it is.</param>
    /// <param name="trackProgress">
    /// False for the evening's look at the last few sessions, so it does not
    /// overwrite the history backfill's progress under the same name.
    /// </param>
    /// <param name="ct">Stops the run.</param>
    public async Task<BackfillReport> RunAsync(DailyDataset dataset, DateOnly from, DateOnly to, Func<bool> mayContinue, bool trackProgress, CancellationToken ct)
    {
        var progress = trackProgress ? _status.Backfill(dataset.Name) : new BackfillProgress(dataset.Name);
        var sessions = Sessions(from, to, _calendar);
        var present = await dataset.DatesPresentAsync(from, to, ct);
        var noFile = await LoadNoFileAsync(dataset.Name, ct);
        var todo = sessions.Where(d => !present.Contains(d) && !noFile.Contains(d)).OrderByDescending(d => d).ToList();

        progress.State = "running";
        progress.LastStartedUtc = Clock();
        progress.RemainingAtStart = todo.Count;
        progress.StoredThisRun = 0;
        progress.LastError = null;

        int stored = 0, notPublished = 0, failed = 0;
        bool paused = false;
        string? lastError = null;
        var noFileBefore = IstTime.DateOf(Clock()).AddDays(-NoFileAfterDays);
        int newNoFile = 0;

        foreach (var day in todo)
        {
            if (!mayContinue())
            {
                paused = true;
                break;
            }

            var outcome = await dataset.FetchDayAsync(day, ct);
            switch (outcome.Result)
            {
                case DayFetchResult.Stored:
                    stored++;
                    progress.StoredThisRun = stored;
                    break;
                case DayFetchResult.NotPublished:
                    notPublished++;
                    if (day <= noFileBefore && noFile.Add(day)) newNoFile++;
                    break;
                default:
                    failed++;
                    lastError = outcome.Error;
                    progress.LastError = lastError;
                    break;
            }

            // Saved as it goes, so a run killed halfway keeps what it learnt.
            if (newNoFile >= 20)
            {
                await SaveNoFileAsync(dataset.Name, noFile, ct);
                newNoFile = 0;
            }
        }

        if (newNoFile > 0) await SaveNoFileAsync(dataset.Name, noFile, ct);

        var report = new BackfillReport(dataset.Name, sessions.Count, todo.Count, stored, notPublished, failed, paused, lastError);
        progress.State = paused ? "waiting" : failed == 0 && stored + notPublished == todo.Count ? "done" : "idle";
        progress.LastFinishedUtc = Clock();
        progress.LastMessage = report.Describe();

        if (todo.Count > 0 && trackProgress)
        {
            if (failed > 0) _logger.LogWarning("Backfill {Report}", report.Describe());
            else _logger.LogInformation("Backfill {Report}", report.Describe());
        }

        return report;
    }

    /// <summary>What is stored between <paramref name="from"/> and <paramref name="to"/>, and how many sessions are still missing.</summary>
    public async Task<BackfillCoverage> CoverageAsync(DailyDataset dataset, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var present = await dataset.DatesPresentAsync(from, to, ct);
        var noFile = await LoadNoFileAsync(dataset.Name, ct);
        int missing = Sessions(from, to, _calendar).Count(d => !present.Contains(d) && !noFile.Contains(d));
        return new BackfillCoverage(dataset.Name,
            present.Count > 0 ? present.Min() : null,
            present.Count > 0 ? present.Max() : null,
            present.Count, missing, noFile.Count(d => d >= from && d <= to));
    }

    private async Task<HashSet<DateOnly>> LoadNoFileAsync(string dataset, CancellationToken ct)
    {
        string? value = await _settings.GetAsync(NoFileKey(dataset), ct);
        var days = new HashSet<DateOnly>();
        foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (DateOnly.TryParseExact(part, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) days.Add(day);
        }

        return days;
    }

    private async Task SaveNoFileAsync(string dataset, HashSet<DateOnly> days, CancellationToken ct)
    {
        // Newest kept first when the column is full: an old day dropped here
        // costs one repeated 404 per run, a new one dropped would count as missing.
        var parts = new List<string>();
        int length = 0;
        foreach (var day in days.OrderByDescending(d => d))
        {
            string text = day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (length + text.Length + 1 > MaxNoFileChars) break;
            parts.Add(text);
            length += text.Length + 1;
        }

        parts.Reverse();
        await _settings.SetAsync(NoFileKey(dataset), string.Join(',', parts), nameof(DailyBackfillRunner), ct);
    }
}
