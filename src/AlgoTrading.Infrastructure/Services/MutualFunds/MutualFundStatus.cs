using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>How the last AMFI daily sync went. Stored as JSON in <see cref="SystemSettingKeys.MutualFundsLastSync"/>.</summary>
public sealed record MutualFundSyncResult
{
    /// <summary>"first", "evening", "morning" or "manual".</summary>
    public string Trigger { get; init; } = string.Empty;

    public DateTime StartedUtc { get; init; }
    public DateTime FinishedUtc { get; init; }
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>The NAV day most of the file's schemes carry: the business day the file is for.</summary>
    public DateOnly? AsOfDate { get; init; }

    /// <summary>The business day whose NAVs were due when the sync ran.</summary>
    public DateOnly? ExpectedDate { get; init; }

    public int Rows { get; init; }
    public int RowsWithoutNav { get; init; }
    public int NewSchemes { get; init; }
    public int NavRowsWritten { get; init; }
}

/// <summary>Where the NAV history backfill stands. Stored as JSON in <see cref="SystemSettingKeys.MutualFundsBackfill"/>.</summary>
public sealed class MutualFundBackfillState
{
    /// <summary>An admin asked for it and it has not finished or been paused: a restart carries on.</summary>
    public bool Requested { get; set; }

    /// <summary>"idle", "waiting" (market hours), "running", "paused", "failed" or "done".</summary>
    public string State { get; set; } = "idle";

    /// <summary>The oldest day wanted; AMFI's history starts on 1 Apr 2006.</summary>
    public DateOnly From { get; set; } = MutualFundHistory.Earliest;

    /// <summary>The last day of the first (newest) window, fixed when the backfill starts.</summary>
    public DateOnly? Top { get; set; }

    /// <summary>The first day of the oldest window finished: the next window ends the day before.</summary>
    public DateOnly? Cursor { get; set; }

    public int WindowsDone { get; set; }
    public long RowsWritten { get; set; }

    /// <summary>Stop after this many windows in a run (a trial); null for no limit.</summary>
    public int? MaxWindows { get; set; }

    public string? LastWindow { get; set; }
    public string? LastError { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>The next window to fetch, or null when the backfill has reached <see cref="From"/>.</summary>
    public (DateOnly From, DateOnly To)? NextWindow()
    {
        if (Top is null) return null;
        var end = Cursor is { } cursor ? cursor.AddDays(-1) : Top.Value;
        if (end < From) return null;
        var start = end.AddDays(-(MutualFundHistory.WindowDays - 1));
        return (start < From ? From : start, end);
    }

    /// <summary>90-day windows between <see cref="From"/> and the next window's end.</summary>
    public int WindowsLeft()
    {
        if (NextWindow() is not { } next) return 0;
        int days = next.To.DayNumber - From.DayNumber + 1;
        return (days + MutualFundHistory.WindowDays - 1) / MutualFundHistory.WindowDays;
    }
}

/// <summary>
/// The process-wide side of the mutual fund jobs: one writer at a time, what
/// is running now, and the backfill's wake-up and stop levers.
/// </summary>
public sealed class MutualFundStatus
{
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Held for each file written, by the daily sync and by the backfill, so the
    /// two never upsert the master at the same time.
    /// </summary>
    public SemaphoreSlim WriteGate { get; } = new(1, 1);

    public bool SyncRunning { get; set; }
    public bool BackfillRunning { get; set; }

    /// <summary>The window and scheme type the backfill is reading now.</summary>
    public string? BackfillNow { get; set; }

    /// <summary>Set by an admin's pause: the backfill stops before its next request.</summary>
    public bool StopBackfill { get; set; }

    /// <summary>Wakes the backfill service now rather than at its next look.</summary>
    public void Wake() => _wake.TrySetResult();

    /// <summary>Completes on <see cref="Wake"/> or after <paramref name="timeout"/>.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
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

/// <summary>Reads and writes the two JSON records in <c>system_settings</c>.</summary>
public static class MutualFundSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<MutualFundSyncResult?> LastSyncAsync(IProcessSettingsStore settings, CancellationToken ct) =>
        Read<MutualFundSyncResult>(await settings.GetAsync(SystemSettingKeys.MutualFundsLastSync, ct));

    public static Task SaveSyncAsync(IProcessSettingsStore settings, MutualFundSyncResult result, CancellationToken ct) =>
        settings.SetAsync(SystemSettingKeys.MutualFundsLastSync,
            JsonSerializer.Serialize(result with { Message = Clip(result.Message, 600) }, Json), nameof(MutualFundNavSync), ct);

    public static async Task<MutualFundBackfillState> BackfillAsync(IProcessSettingsStore settings, CancellationToken ct) =>
        Read<MutualFundBackfillState>(await settings.GetAsync(SystemSettingKeys.MutualFundsBackfill, ct)) ?? new MutualFundBackfillState();

    public static Task SaveBackfillAsync(IProcessSettingsStore settings, MutualFundBackfillState state, CancellationToken ct)
    {
        state.LastError = state.LastError is null ? null : Clip(state.LastError, 600);
        return settings.SetAsync(SystemSettingKeys.MutualFundsBackfill, JsonSerializer.Serialize(state, Json), nameof(MutualFundHistory), ct);
    }

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}
