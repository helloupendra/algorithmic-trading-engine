using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>An import as asked for, validated.</summary>
public sealed record DhanOptionHistoryRequest(
    string Underlying,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<int> StrikeOffsets,
    IReadOnlyList<string> OptionTypes,
    string ExpiryFlag,
    int ExpiryCode,
    string Interval)
{
    /// <summary>Six years: everything Dhan holds (it begins in August 2020), and a bound on a typo.</summary>
    public const int MaxDays = 6 * 366;

    /// <summary>Every (offset, type) pair the import covers.</summary>
    public IReadOnlyList<DhanRollingSeries> Series =>
        StrikeOffsets
            .SelectMany(offset => OptionTypes.Select(type => new DhanRollingSeries(Underlying, ExpiryFlag, ExpiryCode, offset, type, Interval)))
            .ToList();

    /// <exception cref="ArgumentException">Anything the endpoint would refuse or answer with silence.</exception>
    /// <exception cref="NotSupportedException">An underlying or interval the endpoint does not serve.</exception>
    public static DhanOptionHistoryRequest Create(
        string? underlying,
        string? from,
        string? to,
        JsonElement? strikeOffsets,
        IEnumerable<string>? optionTypes,
        string? expiryFlag,
        int? expiryCode,
        string? interval,
        DateOnly todayIst)
    {
        string name = (underlying ?? string.Empty).Trim().ToUpperInvariant();
        _ = DhanRollingOptions.OptionUnderlying(name);

        var fromDate = Date(from, "from");
        var toDate = Date(to, "to");
        if (fromDate > toDate) throw new ArgumentException("from must not be after to.");
        if (toDate >= todayIst)
        {
            // Today's bars are still being written; stored now, the day would
            // count as present and never be fetched whole.
            throw new ArgumentException($"to must be before today ({todayIst:yyyy-MM-dd} IST): a session in progress would be stored half-finished.");
        }
        if (toDate.DayNumber - fromDate.DayNumber + 1 > MaxDays)
            throw new ArgumentException($"At most {MaxDays} days per import.");

        string flag = string.IsNullOrWhiteSpace(expiryFlag) ? "WEEK" : expiryFlag.Trim().ToUpperInvariant();
        if (!DhanRollingOptions.ExpiryFlags.Contains(flag)) throw new ArgumentException("expiryFlag must be WEEK or MONTH.");

        int code = expiryCode ?? 1;
        if (code is < 1 or > 3) throw new ArgumentException("expiryCode must be 1 (nearest), 2 (next) or 3 (far).");

        var types = (optionTypes ?? new[] { "CE", "PE" })
            .Select(t => (t ?? string.Empty).Trim().ToUpperInvariant() switch
            {
                "CE" or "CALL" => "CE",
                "PE" or "PUT" => "PE",
                var other => throw new ArgumentException($"optionTypes: '{other}' is neither CE nor PE."),
            })
            .Distinct()
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();
        if (types.Count == 0) throw new ArgumentException("optionTypes is empty.");

        return new DhanOptionHistoryRequest(
            name,
            fromDate,
            toDate,
            DhanRollingOptions.ParseOffsets(strikeOffsets),
            types,
            flag,
            code,
            DhanRollingOptions.NormalizeInterval(string.IsNullOrWhiteSpace(interval) ? "5" : interval));
    }

    private static DateOnly Date(string? value, string field) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"{field} must be yyyy-MM-dd.");
}

/// <summary>
/// Which (series, window) pairs an import still has to fetch. Pure, so the
/// resume rule is pinned by tests.
/// </summary>
public static class DhanOptionHistoryPlan
{
    public sealed record WorkItem(DhanRollingSeries Series, DateOnly From, DateOnly To);

    public sealed record Result(IReadOnlyList<WorkItem> Work, int Total, int AlreadyPresent, int NoTradingDays);

    /// <param name="presentDays">The days each series already has bars for.</param>
    public static Result Build(
        IReadOnlyList<DhanRollingSeries> series,
        IReadOnlyList<(DateOnly From, DateOnly To)> windows,
        IReadOnlyCollection<DateOnly> tradingDays,
        Func<DhanRollingSeries, IReadOnlySet<DateOnly>> presentDays)
    {
        var work = new List<WorkItem>();
        int present = 0, closed = 0;

        // Window by window, so an interrupted import leaves whole early months
        // rather than whole strikes.
        foreach (var (from, to) in windows)
        {
            bool anyTradingDay = tradingDays.Any(d => d >= from && d <= to);
            foreach (var s in series)
            {
                if (!anyTradingDay)
                {
                    closed++;
                    continue;
                }

                if (DhanRollingOptions.MissingDays(tradingDays, presentDays(s), from, to).Count == 0)
                {
                    present++;
                    continue;
                }

                work.Add(new WorkItem(s, from, to));
            }
        }

        return new Result(work, windows.Count * series.Count, present, closed);
    }

    /// <summary>Weekdays in [from, to]: the fallback calendar when Dhan's index history cannot be read.</summary>
    public static IReadOnlyList<DateOnly> Weekdays(DateOnly from, DateOnly to)
    {
        var days = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Add(d);
        }
        return days;
    }
}

/// <summary>A window that could not be imported.</summary>
public sealed record DhanOptionHistoryError(string Series, DateOnly From, DateOnly To, string Message, DateTime AtUtc);

/// <summary>One import's progress. Written by the runner, read by the console.</summary>
public sealed class DhanOptionHistoryJob
{
    private const int MaxErrors = 100;

    private readonly ConcurrentQueue<DhanOptionHistoryError> _errors = new();
    private int _windowsTotal, _windowsPresent, _windowsClosed, _windowsFetched, _windowsEmpty, _windowsFailed, _requests, _retries, _errorCount;
    private long _rowsFetched, _rowsInserted;

    public DhanOptionHistoryJob(DhanOptionHistoryRequest request)
    {
        Request = request;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public DhanOptionHistoryRequest Request { get; }
    public CancellationTokenSource Cancellation { get; } = new();
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; private set; }
    public DateTime? FinishedUtc { get; private set; }

    /// <summary>queued, planning, running, completed, cancelled, interrupted, failed.</summary>
    public string Status { get; private set; } = "queued";

    /// <summary>Why it failed or stopped, or a note about how it ran.</summary>
    public string? Message { get; private set; }

    /// <summary>Trading days in the range, and whether they came from the exchange's calendar.</summary>
    public int TradingDays { get; private set; }
    public string CalendarSource { get; private set; } = "pending";

    public bool IsFinished => FinishedUtc is not null;

    internal void Planning()
    {
        StartedUtc = DateTime.UtcNow;
        Status = "planning";
    }

    internal void Planned(DhanOptionHistoryPlan.Result plan, int tradingDays, string calendarSource, int calendarRequests)
    {
        _windowsTotal = plan.Total;
        _windowsPresent = plan.AlreadyPresent;
        _windowsClosed = plan.NoTradingDays;
        TradingDays = tradingDays;
        CalendarSource = calendarSource;
        Interlocked.Add(ref _requests, calendarRequests);
        Status = "running";
    }

    internal void Note(string message) => Message = message;

    internal void Retried() => Interlocked.Increment(ref _retries);

    internal void WindowDone(DhanOptionWindowResult result)
    {
        Interlocked.Add(ref _requests, 1 + result.Retries);
        Interlocked.Increment(ref _windowsFetched);
        if (result.Empty) Interlocked.Increment(ref _windowsEmpty);
        Interlocked.Add(ref _rowsFetched, result.Fetched);
        Interlocked.Add(ref _rowsInserted, result.Inserted);
    }

    internal void WindowFailed(DhanRollingSeries series, DateOnly from, DateOnly to, string message)
    {
        Interlocked.Increment(ref _requests);
        Interlocked.Increment(ref _windowsFailed);
        if (Interlocked.Increment(ref _errorCount) <= MaxErrors)
            _errors.Enqueue(new DhanOptionHistoryError(series.ToString(), from, to, message, DateTime.UtcNow));
    }

    internal void Finish(string status, string? message = null)
    {
        if (IsFinished) return;
        Status = status;
        if (message is not null) Message = message;
        FinishedUtc = DateTime.UtcNow;
    }

    /// <summary>A consistent reading for the console.</summary>
    public object View()
    {
        int total = _windowsTotal, present = _windowsPresent, closed = _windowsClosed, fetched = _windowsFetched, failed = _windowsFailed;
        int done = present + closed + fetched;
        var elapsed = ((FinishedUtc ?? DateTime.UtcNow) - (StartedUtc ?? DateTime.UtcNow)).TotalSeconds;
        int toFetch = total - present - closed;
        int remaining = Math.Max(0, total - done - failed);
        double? eta = !IsFinished && fetched + failed > 0 && elapsed > 0 ? Math.Round(remaining * elapsed / (fetched + failed)) : null;

        return new
        {
            id = Id,
            status = Status,
            message = Message,
            request = new
            {
                underlying = Request.Underlying,
                from = Request.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                to = Request.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                strikeOffsets = Request.StrikeOffsets,
                optionTypes = Request.OptionTypes,
                expiryFlag = Request.ExpiryFlag,
                expiryCode = Request.ExpiryCode,
                interval = Request.Interval,
                resolution = DhanRollingOptions.ResolutionFor(Request.Interval),
            },
            createdUtc = CreatedUtc,
            startedUtc = StartedUtc,
            finishedUtc = FinishedUtc,
            elapsedSeconds = Math.Round(elapsed, 1),
            tradingDays = TradingDays,
            calendarSource = CalendarSource,
            windows = new
            {
                total,
                done,
                remaining,
                // Of done: already stored before this job, no trading day in the window, fetched now.
                alreadyPresent = present,
                noTradingDays = closed,
                fetched,
                // Of fetched: Dhan answered no bars for a window with trading days.
                empty = _windowsEmpty,
                failed,
                toFetch,
            },
            rows = new { fetched = _rowsFetched, inserted = _rowsInserted },
            requests = _requests,
            retries = _retries,
            etaSeconds = eta,
            errorCount = _errorCount,
            errors = _errors.ToArray(),
        };
    }
}

/// <summary>
/// The import queue and every job's state, for the life of the process. A
/// restart loses the jobs, not the data: posting the same import again skips
/// every window already stored.
/// </summary>
public sealed class DhanOptionHistoryJobs
{
    private const int Keep = 50;

    private readonly ConcurrentDictionary<Guid, DhanOptionHistoryJob> _jobs = new();
    private readonly Channel<DhanOptionHistoryJob> _queue = Channel.CreateUnbounded<DhanOptionHistoryJob>(new UnboundedChannelOptions { SingleReader = true });

    public DhanOptionHistoryJob Enqueue(DhanOptionHistoryRequest request)
    {
        var job = new DhanOptionHistoryJob(request);
        _jobs[job.Id] = job;
        _queue.Writer.TryWrite(job);

        foreach (var old in _jobs.Values.Where(j => j.IsFinished).OrderByDescending(j => j.CreatedUtc).Skip(Keep))
            _jobs.TryRemove(old.Id, out _);

        return job;
    }

    public DhanOptionHistoryJob? Get(Guid id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public IReadOnlyList<DhanOptionHistoryJob> All() => _jobs.Values.OrderByDescending(j => j.CreatedUtc).ToList();

    internal ChannelReader<DhanOptionHistoryJob> Reader => _queue.Reader;
}

/// <summary>
/// Runs queued imports one at a time, each over several lanes that share
/// <see cref="DhanRateGate"/>: Dhan answers a window in 0.2 to 4.5 seconds, so a
/// single lane would spend most of its budget waiting on replies.
/// </summary>
public sealed class DhanOptionHistoryWorker : BackgroundService
{
    /// <summary>Concurrent requests. The gate still spaces them 220 ms apart.</summary>
    internal const int Lanes = 4;

    private readonly IServiceScopeFactory _scopes;
    private readonly DhanOptionHistoryJobs _jobs;
    private readonly ILogger<DhanOptionHistoryWorker> _logger;

    public DhanOptionHistoryWorker(IServiceScopeFactory scopes, DhanOptionHistoryJobs jobs, ILogger<DhanOptionHistoryWorker> logger)
    {
        _scopes = scopes;
        _jobs = jobs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _jobs.Reader.ReadAllAsync(stoppingToken))
            {
                if (job.Cancellation.IsCancellationRequested)
                {
                    job.Finish("cancelled", "cancelled before it started");
                    continue;
                }

                await RunAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task RunAsync(DhanOptionHistoryJob job, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancellation.Token);
        var ct = linked.Token;
        var request = job.Request;
        job.Planning();
        _logger.LogInformation("Dhan option history import {Job} started: {Underlying} {From}..{To} offsets {Offsets} {Types} {Flag}{Code} {Interval}m.",
            job.Id, request.Underlying, request.From, request.To, string.Join(",", request.StrikeOffsets), string.Join(",", request.OptionTypes),
            request.ExpiryFlag, request.ExpiryCode, request.Interval);

        try
        {
            var series = request.Series;
            var windows = DhanRollingOptions.Windows(request.From, request.To);
            IReadOnlyList<DateOnly> tradingDays;
            string calendarSource = "dhan-index-daily";
            var present = new Dictionary<DhanRollingSeries, IReadOnlySet<DateOnly>>();

            await using (var scope = _scopes.CreateAsyncScope())
            {
                var importer = scope.ServiceProvider.GetRequiredService<DhanOptionHistoryImporter>();
                try
                {
                    tradingDays = await importer.TradingDaysAsync(request.Underlying, request.From, request.To, ct);
                }
                catch (DhanApiException ex) when (!ex.IsAuthFailure && !ex.IsNotSubscribed)
                {
                    // Without the exchange's calendar every weekday is expected:
                    // holidays are then fetched, found empty, and counted done.
                    tradingDays = DhanOptionHistoryPlan.Weekdays(request.From, request.To);
                    calendarSource = "weekdays";
                    job.Note($"The index's daily history could not be read ({ex.Message}); every weekday is treated as a trading day.");
                }

                foreach (var s in series)
                    present[s] = await importer.PresentDaysAsync(s, request.From, request.To, ct);
            }

            var plan = DhanOptionHistoryPlan.Build(series, windows, tradingDays, s => present[s]);
            int calendarRequests = calendarSource == "weekdays" ? 0 : Math.Max(1, (int)Math.Ceiling((request.To.DayNumber - request.From.DayNumber + 1) / 365.0));
            job.Planned(plan, tradingDays.Count, calendarSource, calendarRequests);

            await Parallel.ForEachAsync(
                plan.Work,
                new ParallelOptions { MaxDegreeOfParallelism = Lanes, CancellationToken = ct },
                async (item, token) =>
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var importer = scope.ServiceProvider.GetRequiredService<DhanOptionHistoryImporter>();
                    try
                    {
                        var result = await importer.ImportWindowAsync(item.Series, item.From, item.To, _ => job.Retried(), token);
                        job.WindowDone(result);
                    }
                    catch (DhanApiException ex) when (ex.IsAuthFailure || ex.IsNotSubscribed)
                    {
                        // Every later window would be refused the same way.
                        job.WindowFailed(item.Series, item.From, item.To, ex.Message);
                        job.Finish("failed", ex.Message);
                        linked.Cancel();
                    }
                    catch (Exception ex) when (!token.IsCancellationRequested)
                    {
                        job.WindowFailed(item.Series, item.From, item.To, ex.Message);
                    }
                });

            job.Finish("completed");
        }
        catch (OperationCanceledException)
        {
            if (job.Cancellation.IsCancellationRequested) job.Finish("cancelled", "cancelled by an admin; posting the same import again resumes it");
            else if (stoppingToken.IsCancellationRequested) job.Finish("interrupted", "the API stopped; posting the same import again resumes it");
            else job.Finish("failed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dhan option history import {Job} failed.", job.Id);
            job.Finish("failed", ex.Message);
        }

        _logger.LogInformation("Dhan option history import {Job} {Status}: {View}", job.Id, job.Status, JsonSerializer.Serialize(job.View()));
    }
}
