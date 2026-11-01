using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>One run of a fund-report job.</summary>
public sealed record MutualFundJobRun
{
    /// <summary>"first", "evening", "morning", "retry", "requested" or "backfill".</summary>
    public string Trigger { get; init; } = string.Empty;

    public DateTime StartedUtc { get; init; }
    public DateTime FinishedUtc { get; init; }
    public bool Ok { get; init; }
    public string Message { get; init; } = string.Empty;

    /// <summary>The newest day the run's data reaches: the TRIs' last common day, the TER's last day, the ranking's NAV day.</summary>
    public DateOnly? Reached { get; init; }

    public int Rows { get; init; }
}

/// <summary>One index's history fetch: how far back it has come.</summary>
public sealed class MutualFundBenchmarkHistory
{
    /// <summary>The first day of the oldest window fetched; the next window ends the day before.</summary>
    public DateOnly? Cursor { get; set; }

    /// <summary>Back to <see cref="MutualFundsOptions.BenchmarkFrom"/>, or to the index's first value.</summary>
    public bool Complete { get; set; }

    public string? Note { get; set; }
}

/// <summary>
/// The fund report's nightly jobs: the benchmark sync and its one-time history, the TER sync and the category
/// ranking. Stored as JSON in <c>system_settings</c> (<see cref="MutualFundAnalyticsSettings"/>), so a restart neither
/// repeats a run nor forgets a failure.
/// </summary>
public sealed class MutualFundAnalyticsState
{
    public MutualFundJobRun? Benchmarks { get; set; }
    public Dictionary<string, MutualFundBenchmarkHistory> BenchmarkHistory { get; set; } = new(StringComparer.Ordinal);
    public MutualFundJobRun? Ter { get; set; }
    public MutualFundJobRun? Stats { get; set; }

    /// <summary>The NAV backfill's cursor when the ranking last ran: as older NAVs arrive, more schemes have 3 and 5 years.</summary>
    public DateOnly? StatsBackfillCursor { get; set; }

    /// <summary>Jobs an admin asked for ("benchmarks", "ter", "stats"): they run at the next look outside market hours.</summary>
    public List<string> Requested { get; set; } = new();
}

/// <summary>The three jobs' names, as the API and the console use them.</summary>
public static class MutualFundJobs
{
    public const string Benchmarks = "benchmarks";
    public const string Ter = "ter";
    public const string Stats = "stats";

    public static readonly IReadOnlyList<string> All = [Benchmarks, Ter, Stats];
}

/// <summary>
/// Reads and writes <see cref="MutualFundAnalyticsState"/>, one <c>system_settings</c> key per part
/// (<see cref="SystemSettingKeys.MutualFundsAnalytics"/>), so no value nears the column's 2,000 characters.
/// </summary>
public static class MutualFundAnalyticsSettings
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string BenchmarksKey = SystemSettingKeys.MutualFundsAnalytics + ".benchmarks";
    public const string HistoryKey = SystemSettingKeys.MutualFundsAnalytics + ".benchmarks.history";
    public const string TerKey = SystemSettingKeys.MutualFundsAnalytics + ".ter";
    public const string StatsKey = SystemSettingKeys.MutualFundsAnalytics + ".stats";
    public const string RequestedKey = SystemSettingKeys.MutualFundsAnalytics + ".requested";

    /// <summary>The ranking's record: its last run and the NAV backfill's cursor at the time.</summary>
    private sealed record StatsRecord(MutualFundJobRun? Run, DateOnly? BackfillCursor);

    public static async Task<MutualFundAnalyticsState> ReadAsync(IProcessSettingsStore settings, CancellationToken ct)
    {
        var stats = Read<StatsRecord>(await settings.GetAsync(StatsKey, ct));
        return new MutualFundAnalyticsState
        {
            Benchmarks = Read<MutualFundJobRun>(await settings.GetAsync(BenchmarksKey, ct)),
            BenchmarkHistory = new Dictionary<string, MutualFundBenchmarkHistory>(
                Read<Dictionary<string, MutualFundBenchmarkHistory>>(await settings.GetAsync(HistoryKey, ct)) ?? new(), StringComparer.Ordinal),
            Ter = Read<MutualFundJobRun>(await settings.GetAsync(TerKey, ct)),
            Stats = stats?.Run,
            StatsBackfillCursor = stats?.BackfillCursor,
            Requested = Read<List<string>>(await settings.GetAsync(RequestedKey, ct)) ?? new(),
        };
    }

    /// <summary>
    /// Re-reads the state, applies <paramref name="change"/> and writes back only the parts it changed, so two
    /// writers touching different parts never undo each other.
    /// </summary>
    public static async Task<MutualFundAnalyticsState> UpdateAsync(IProcessSettingsStore settings, Action<MutualFundAnalyticsState> change, CancellationToken ct)
    {
        var state = await ReadAsync(settings, ct);
        var before = Parts(state);
        change(state);
        foreach (var (key, json) in Parts(state))
        {
            if (before[key] != json) await settings.SetAsync(key, json, "MutualFundAnalytics", ct);
        }

        return state;
    }

    private static Dictionary<string, string> Parts(MutualFundAnalyticsState s) => new(StringComparer.Ordinal)
    {
        [BenchmarksKey] = JsonSerializer.Serialize(s.Benchmarks, Json),
        [HistoryKey] = JsonSerializer.Serialize(s.BenchmarkHistory, Json),
        [TerKey] = JsonSerializer.Serialize(s.Ter, Json),
        [StatsKey] = JsonSerializer.Serialize(new StatsRecord(s.Stats, s.StatsBackfillCursor), Json),
        [RequestedKey] = JsonSerializer.Serialize(s.Requested, Json),
    };

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

    /// <summary>A run's message, clipped for storage.</summary>
    public static string Clip(string text) => text.Length <= 600 ? text : text[..600] + "…";
}

/// <summary>
/// When each fund-report job is due. Pure functions of the clock and the last run, so the schedule is tested
/// without a host.
/// </summary>
/// <remarks>
/// The jobs run after the 23:30 IST NAV sync: at 23:45 IST every day (the benchmark sync and the TER sync), again at
/// 07:15 IST when the evening run failed, and an hour after any failure. The category ranking runs when a newer full
/// NAV day is stored than the one it ranked, and, while the NAV history backfill is filling older years, again at most
/// every six hours. Nothing runs 09:00–15:40 IST on trading days (the caller checks the quiet window).
/// </remarks>
public static class MutualFundAnalyticsSchedule
{
    public static readonly TimeSpan EveningAt = new(23, 45, 0);
    public static readonly TimeSpan MorningRetryAt = new(7, 15, 0);
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(1);

    /// <summary>The ranking reruns for an advancing NAV backfill at most this often.</summary>
    public static readonly TimeSpan StatsBackfillEvery = TimeSpan.FromHours(6);

    /// <summary>
    /// Whether a nightly job is due: "first" when it has never run, "evening" once today's 23:45 IST has passed
    /// without a run since, "morning" once 07:15 IST has passed without a run since and the last run failed or fell
    /// short of <paramref name="expected"/>, "retry" an hour after a failure.
    /// </summary>
    public static string? NightlyDue(DateTime nowUtc, MutualFundJobRun? last, DateOnly? expected = null)
    {
        if (last is null) return "first";
        var today = IstTime.DateOf(nowUtc);
        var evening = IstTime.StartOfDayUtc(today) + EveningAt;
        var morning = IstTime.StartOfDayUtc(today) + MorningRetryAt;

        if (nowUtc >= evening && last.StartedUtc < evening) return "evening";
        bool behind = !last.Ok || (expected is { } day && (last.Reached is null || last.Reached < day));
        if (nowUtc >= morning && last.StartedUtc < morning && behind) return "morning";
        if (!last.Ok && nowUtc - last.StartedUtc >= RetryFailedAfter) return "retry";
        return null;
    }

    /// <summary>
    /// Whether the category ranking is due: never run; a newer full NAV day than the one ranked; or older NAVs stored
    /// by the backfill since the last run, at most every <see cref="StatsBackfillEvery"/>; and an hour after a failure.
    /// </summary>
    public static string? StatsDue(DateTime nowUtc, MutualFundJobRun? last, DateOnly? latestFullNavDay, DateOnly? backfillCursor, DateOnly? cursorAtLastRun)
    {
        if (latestFullNavDay is null) return null;
        if (last is null) return "first";
        if (!last.Ok) return nowUtc - last.StartedUtc >= RetryFailedAfter ? "retry" : null;
        if (last.Reached is null || last.Reached < latestFullNavDay) return "evening";
        bool olderNavs = backfillCursor is { } cursor && (cursorAtLastRun is null || cursor < cursorAtLastRun);
        return olderNavs && nowUtc - last.StartedUtc >= StatsBackfillEvery ? "backfill" : null;
    }
}
