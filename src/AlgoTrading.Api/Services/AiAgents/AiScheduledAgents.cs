using System.Collections.Concurrent;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>An agent the desk runs on its own schedule: it finds what is due and does one piece of it.</summary>
public interface IAiScheduledAgent
{
    string AgentKey { get; }

    /// <summary>Does at most one unit of due work (one run, one batch, one incident). True when it did something.</summary>
    Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Writes (or rewrites) the report on one subject now, whatever the
    /// schedule says: the AI page's "run now". Null when the agent works in
    /// batches and <paramref name="subjectId"/> is null: it then runs its next batch.
    /// </summary>
    Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken);
}

/// <summary>What the scheduler remembers between ticks: when each agent last worked. In memory; a restart only makes the next batch come sooner.</summary>
public sealed class AiSchedulerState
{
    private readonly ConcurrentDictionary<string, DateTime> _lastWork = new(StringComparer.Ordinal);

    public DateTime? LastWork(string agent) => _lastWork.TryGetValue(agent, out var at) ? at : null;

    public void Worked(string agent, DateTime atUtc) => _lastWork[agent] = atUtc;
}

/// <summary>
/// Writes an agent's report on one subject, one row per agent and subject,
/// counting tries so a subject that keeps failing is given up on.
/// </summary>
public sealed class AiReportWriter(TradingDbContext db, IOptionsMonitor<AiSettings> settings, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>A failed report is tried again after this long, up to <see cref="AiSettings.MaxReportAttempts"/> tries.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The subject ids of <paramref name="candidates"/> the agent still has to
    /// write: no report yet, or a failed one that is due another try.
    /// </summary>
    public async Task<HashSet<string>> DueAsync(string agent, string subjectType, IReadOnlyCollection<string> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return [];
        var now = _time.GetUtcNow().UtcDateTime;
        int maxAttempts = settings.CurrentValue.MaxReportAttempts;

        var existing = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == agent && r.SubjectType == subjectType && candidates.Contains(r.SubjectId))
            .Select(r => new { r.SubjectId, r.Status, r.Attempts, r.UpdatedUtc })
            .ToListAsync(cancellationToken);
        var done = existing
            .Where(r => r.Status != AiReportStatus.Failed || r.Attempts >= maxAttempts || now - r.UpdatedUtc < RetryAfter)
            .Select(r => r.SubjectId)
            .ToHashSet(StringComparer.Ordinal);

        return candidates.Where(c => !done.Contains(c)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The subject ids of <paramref name="candidates"/> whose report is not
    /// settled: none written yet, or a failed one with tries left, whether its
    /// next try is due now or after <see cref="RetryAfter"/>.
    /// </summary>
    public async Task<HashSet<string>> UnsettledAsync(string agent, string subjectType, IReadOnlyCollection<string> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return [];
        int maxAttempts = settings.CurrentValue.MaxReportAttempts;

        var settled = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == agent && r.SubjectType == subjectType && candidates.Contains(r.SubjectId)
                        && (r.Status != AiReportStatus.Failed || r.Attempts >= maxAttempts))
            .Select(r => r.SubjectId)
            .ToListAsync(cancellationToken);

        return candidates.Except(settled, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Creates or updates the report; a successful one is never overwritten by a failure.</summary>
    public async Task<AiReport> SaveAsync(
        string agent,
        string subjectType,
        string subjectId,
        DateOnly? sessionDate,
        string status,
        AiAskResult? call,
        string title,
        string body,
        string dataJson,
        string error,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var row = await db.AiReports.FirstOrDefaultAsync(
            r => r.AgentKey == agent && r.SubjectType == subjectType && r.SubjectId == subjectId, cancellationToken);
        if (row is null)
        {
            row = new AiReport { AgentKey = agent, SubjectType = subjectType, SubjectId = subjectId, CreatedUtc = now };
            db.AiReports.Add(row);
        }
        else if (row.Status != AiReportStatus.Failed && status == AiReportStatus.Failed)
        {
            // A good report stays; the failed retry is on the Calls tab.
            return row;
        }

        row.SessionDate = sessionDate;
        row.Status = status;
        row.Attempts++;
        row.CallId = call?.CallId ?? row.CallId;
        row.Model = call?.Model ?? string.Empty;
        row.Title = Cut(title, 300);
        row.Body = body;
        row.DataJson = string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson;
        row.Error = error;
        row.UpdatedUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return row;
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>
/// Runs the scheduled agents: once a minute, each switched-on agent does one
/// piece of its due work, one agent after another.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here waits on the trading path: the agents read the desk and write
/// reports, and a slow or failing model only delays a report. One agent's
/// failure is logged and the next agent still runs.
/// </para>
/// <para>
/// The switch is checked every tick, so turning an agent off on the AI page
/// stops it within a minute. <see cref="AiSettings.SchedulerEnabled"/> stops
/// all of them on one API (the Mac's, say), and nothing runs without a key.
/// </para>
/// </remarks>
public sealed class AiAgentScheduler(
    IServiceScopeFactory scopes,
    IOptionsMonitor<AiSettings> settings,
    ILogger<AiAgentScheduler> logger,
    TimeProvider? time = null) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    /// <summary>After a start, the API settles (migrations, feeds re-adopted) before any agent calls out.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(90);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, _time, stoppingToken);
            using var timer = new PeriodicTimer(Tick, _time);
            do
            {
                try
                {
                    await RunTickAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // Never let the AI take the API down: an unhandled exception
                    // in a hosted service stops the host.
                    logger.LogError(ex, "The AI agent scheduler's tick failed; it tries again next minute");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The API is stopping.
        }
    }

    /// <summary>One tick: each switched-on agent does one piece of due work. Public for tests.</summary>
    public async Task RunTickAsync(CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        if (!s.SchedulerEnabled || !s.KeyConfigured) return;

        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<AiSettingsStore>();
        var state = await store.LoadAsync(cancellationToken);

        foreach (var agent in scope.ServiceProvider.GetServices<IAiScheduledAgent>())
        {
            if (!IsOn(agent.AgentKey, state, s)) continue;
            try
            {
                await agent.RunOnceAsync(_time.GetUtcNow().UtcDateTime, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "AI agent {Agent} failed its scheduled work", agent.AgentKey);
            }
        }
    }

    /// <summary>
    /// An agent's switch; the assistant check and exam, utilities with no switch
    /// of their own, run while enabled in settings and the Desk Assistant is on.
    /// The daily digest runs while it is enabled in settings, whichever agents
    /// are on: it reports on them and asks no model.
    /// </summary>
    public static bool IsOn(string key, AiState state, AiSettings settings) => key switch
    {
        AiCatalog.AssistantCheck => settings.AssistantCheckEnabled && state.Agent(AiCatalog.DeskAssistant) is { Status: "on" },
        AiCatalog.AssistantExam => settings.ExamEnabled && state.Agent(AiCatalog.DeskAssistant) is { Status: "on" },
        AiCatalog.DailyDigest => settings.ReviewDigestToTelegram,
        _ => state.Agent(key) is { Status: "on" },
    };
}
