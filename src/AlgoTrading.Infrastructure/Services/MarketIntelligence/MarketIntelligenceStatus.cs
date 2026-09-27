using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>The recorders and backfills, by the names the status endpoint reports them under.</summary>
public static class MarketIntelligenceNames
{
    public const string News = "news";
    public const string Announcements = "announcements";
    public const string Calendar = "calendar";
    public const string QuoteSnapshots = "quote-snapshots";
    public const string GlobalDaily = "global-daily";
    public const string Breadth = "breadth";
    public const string ParticipantOi = "participant-oi";
    public const string NewsScoring = "news-scoring";

    /// <summary>The datasets an admin can backfill by name.</summary>
    public static readonly IReadOnlyList<string> Backfills = [Breadth, ParticipantOi, GlobalDaily];
}

/// <summary>How one recorder has been doing since the API started.</summary>
/// <remarks>
/// A recorder reads many sources (33 feeds, 17 prices); one failing is logged
/// when it starts failing and when it recovers, not on every poll, which is
/// what <see cref="SourceFailed"/> and <see cref="SourceRecovered"/> decide.
/// </remarks>
public sealed class RecorderHealth
{
    private readonly ConcurrentDictionary<string, string> _failingSources = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public RecorderHealth(string name) => Name = name;

    public string Name { get; }
    public DateTime? LastAttemptUtc { get; private set; }
    public DateTime? LastSuccessUtc { get; private set; }
    public string? LastMessage { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? LastErrorUtc { get; private set; }

    /// <summary>Sources failing right now, with their error.</summary>
    public IReadOnlyDictionary<string, string> FailingSources => new Dictionary<string, string>(_failingSources);

    public void Attempted(DateTime utc)
    {
        lock (_gate) LastAttemptUtc = utc;
    }

    /// <summary>A run finished; with <paramref name="error"/> it failed as a whole.</summary>
    public void Finished(DateTime utc, string message, string? error = null)
    {
        lock (_gate)
        {
            LastMessage = message;
            if (error is null)
            {
                LastSuccessUtc = utc;
            }
            else
            {
                LastError = error;
                LastErrorUtc = utc;
            }
        }
    }

    /// <summary>
    /// Records a source's failure. True when it is news: the source was fine
    /// before, or now fails differently. Only then is it worth a log line.
    /// </summary>
    public bool SourceFailed(string source, string error, DateTime utc)
    {
        lock (_gate)
        {
            LastError = $"{source}: {error}";
            LastErrorUtc = utc;
        }

        bool changed = !_failingSources.TryGetValue(source, out var previous) || previous != error;
        _failingSources[source] = error;
        return changed;
    }

    /// <summary>Clears a source's failure. True when it had been failing, so the recovery is logged once.</summary>
    public bool SourceRecovered(string source) => _failingSources.TryRemove(source, out _);

    /// <summary>
    /// Logs a source's failure the first time it happens (or changes), and at
    /// debug level after that, so a feed that is down all day costs one warning.
    /// </summary>
    public void LogSourceFailure(ILogger logger, string source, string error, DateTime utc)
    {
        if (SourceFailed(source, error, utc))
            logger.LogWarning("{Recorder}: {Source} failed: {Error}. Logged once until it recovers or fails differently.", Name, source, error);
        else
            logger.LogDebug("{Recorder}: {Source} still failing: {Error}", Name, source, error);
    }

    public void LogSourceRecovery(ILogger logger, string source)
    {
        if (SourceRecovered(source)) logger.LogInformation("{Recorder}: {Source} recovered.", Name, source);
    }
}

/// <summary>Where one dataset's history backfill stands.</summary>
public sealed class BackfillProgress
{
    public BackfillProgress(string dataset) => Dataset = dataset;

    public string Dataset { get; }

    /// <summary>"idle", "running", "waiting" (for the quiet window to end) or "done".</summary>
    public string State { get; set; } = "idle";

    public DateTime? LastStartedUtc { get; set; }
    public DateTime? LastFinishedUtc { get; set; }
    public string? LastMessage { get; set; }
    public string? LastError { get; set; }

    /// <summary>Days still to fetch when the current or last run started.</summary>
    public int? RemainingAtStart { get; set; }

    /// <summary>Days fetched and stored by the current or last run.</summary>
    public int StoredThisRun { get; set; }

    /// <summary>An admin asked for a run; the backfill service takes it at its next look.</summary>
    public bool Requested { get; set; }
}

/// <summary>Process-wide record of the market-intelligence recorders and backfills, for the status endpoint.</summary>
public sealed class MarketIntelligenceStatus
{
    private readonly ConcurrentDictionary<string, RecorderHealth> _recorders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BackfillProgress> _backfills = new(StringComparer.OrdinalIgnoreCase);
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When this API process started recording; before a recorder's first run, "no success yet" is not a fault.</summary>
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;

    public RecorderHealth Recorder(string name) => _recorders.GetOrAdd(name, key => new RecorderHealth(key));

    public BackfillProgress Backfill(string dataset) => _backfills.GetOrAdd(dataset, key => new BackfillProgress(key));

    /// <summary>
    /// Wakes the backfill service for these datasets. It still waits for the
    /// quiet window to end; the request is kept until it runs.
    /// </summary>
    public void RequestBackfill(IEnumerable<string> datasets)
    {
        foreach (var dataset in datasets) Backfill(dataset).Requested = true;
        _wake.TrySetResult();
    }

    /// <summary>Completes when an admin asks for a backfill, or after <paramref name="timeout"/>.</summary>
    public async Task WaitForRequestAsync(TimeSpan timeout, CancellationToken ct)
    {
        var wake = _wake;
        try
        {
            await wake.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
        }

        if (wake.Task.IsCompleted) Interlocked.CompareExchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), wake);
    }
}
