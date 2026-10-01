using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.Today;

/// <summary>
/// The owner's one page (1 Oct: "mai busy rhta hu, mujhe ek section chaiye jaha mai sb dkhlu"): what needs a look,
/// today's trading, what each AI agent did, what they learnt, the system, and the decisions on record. Read-only:
/// nothing here waits for an answer.
/// </summary>
/// <remarks>
/// Built from what the desk already keeps: the live run history (recaps left out), ai_calls and ai_reports, the
/// memories, Sentinel's incidents, heartbeat and checkups, the desk's deploy history, and the decision log
/// (<c>owner_decisions</c>).
/// </remarks>
public sealed class TodayBuilder(
    TradingDbContext db,
    LiveRunHistoryBuilder history,
    IMarketSessionService sessions,
    AiSettingsStore aiStore,
    IWebHostEnvironment environment,
    TimeProvider? time = null,
    IOptionsMonitor<AiSettings>? aiSettings = null)
{
    /// <summary>The decisions the page shows, newest first.</summary>
    public const int DecisionsShown = 100;

    /// <summary>Sentinel's round is late past this.</summary>
    public static readonly TimeSpan SentinelLate = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<TodayResponse> BuildAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var today = IstTime.DateOf(now);
        var dayStart = IstTime.StartOfDayUtc(today);

        var markets = new[] { Market(now, MarketCloseRules.Nse, "FO"), Market(now, MarketCloseRules.Mcx, "COM") };
        var trading = await TradingAsync(today, cancellationToken);
        var agents = await AgentsAsync(dayStart, cancellationToken);
        var learning = await LearningAsync(today, dayStart, cancellationToken);
        var system = await SystemAsync(cancellationToken);
        var decisions = await DecisionsAsync(cancellationToken);
        var incidents = await db.Incidents.AsNoTracking()
            .Where(i => IncidentStatus.Live.Contains(i.Status))
            .OrderByDescending(i => i.LastSeenUtc)
            .Select(i => new { i.Id, i.Severity, i.Title, i.Summary, i.FirstSeenUtc, i.LastSeenUtc })
            .ToListAsync(cancellationToken);
        var explained = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AiCatalog.IncidentExplainer && r.SubjectType == AiReportSubject.Incident && r.Status == AiReportStatus.Ok)
            .Select(r => new { r.SubjectId, r.Title })
            .ToListAsync(cancellationToken);

        var attention = new List<TodayAttention>();
        foreach (var i in incidents.Where(i => i.Severity is IncidentSeverity.Critical or IncidentSeverity.High or IncidentSeverity.Medium))
        {
            string? ai = explained.FirstOrDefault(r => r.SubjectId == i.Id.ToString(CultureInfo.InvariantCulture))?.Title;
            attention.Add(new TodayAttention(i.Severity, "incident", i.Title, ai is null ? Cut(i.Summary, 200) : $"AI: {ai}",
                $"/system/incidents?id={i.Id}", Utc(i.LastSeenUtc)));
        }

        if (learning.CheckToday is { } check && check.Total > 0 && (double)check.Passed / check.Total < Api.Services.AiAgents.AssistantCheckAgent.PassMark)
        {
            attention.Add(new TodayAttention("high", "check", $"The Assistant check passed {check.Passed} of {check.Total}",
                "Below the 80% pass mark: a model or prompt change may be misreading the desk.", "/ai/reports", null));
        }

        foreach (var a in agents.Where(a => a.On && (a.ReportsToday.Failed > 0 || a.FailedCallsToday >= 3)))
        {
            attention.Add(new TodayAttention("medium", "agent", $"{a.Name}: {a.ReportsToday.Failed} failed reports, {a.FailedCallsToday} failed calls today",
                "Usually the free tier queueing; it retries by itself.", "/ai/calls", a.LastActivityUtc));
        }

        if (system.SentinelLastUtc is DateTime beat && now - beat > SentinelLate)
        {
            attention.Add(new TodayAttention("high", "system", $"Sentinel's last round was {(int)(now - beat).TotalMinutes} min ago",
                "The watchman may have stopped; incidents are not being raised.", "/system/incidents", beat));
        }

        foreach (var l in learning.LearnedToday)
        {
            attention.Add(new TodayAttention("info", "learning", $"{l.AgentName} learned M{l.Id}", $"{l.Text} ({l.How})", $"/ai/memory#memory-{l.Id}", null));
        }

        foreach (var d in decisions.Where(d => d.Status == "open"))
        {
            attention.Add(new TodayAttention("info", "decision", d.Title, d.Decided, null, null));
        }

        var aiTrader = await AiTraderAsync(today, cancellationToken);
        if (aiTrader is { Status: "on", NoAnswer: >= 3 })
        {
            attention.Add(new TodayAttention("medium", "ai-trader", $"The AI Trader had no usable answer {aiTrader.NoAnswer} times today",
                "The model did not answer, or not in the shape asked; those looks decided nothing.", "/ai/agents?agent=ai-trader", null));
        }

        var order = new Dictionary<string, int> { ["critical"] = 0, ["high"] = 1, ["medium"] = 2, ["info"] = 3 };
        attention = attention.OrderBy(a => order.GetValueOrDefault(a.Level, 4)).ThenByDescending(a => a.AtUtc).ToList();

        return new TodayResponse(today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), now, markets, attention, trading, agents, learning,
            system with { OpenIncidents = incidents.Count }, decisions)
        {
            AiTrader = aiTrader,
        };
    }

    /// <summary>The AI Trader today: on or off, shadow or placing, its looks and the last three decisions. Null until it has ever looked or been switched on.</summary>
    private async Task<TodayAiTrader?> AiTraderAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var state = await aiStore.LoadAsync(cancellationToken);
        string status = state.Agent(AiCatalog.AiTrader)?.Status ?? "off";
        var rows = await db.AiTraderDecisions.AsNoTracking()
            .Where(d => d.Day == today && d.ReplaySessionId == null)
            .OrderByDescending(d => d.ClockUtc)
            .Select(d => new { d.ClockUtc, d.Mode, d.Action, d.Underlying, d.Allowed, d.Rule, d.Why, d.Reason, d.PlanJson })
            .ToListAsync(cancellationToken);
        if (status != "on" && rows.Count == 0) return null;
        var shadow = await db.AiTraderShadowPositions.AsNoTracking()
            .Where(p => p.Day == today && p.ReplaySessionId == null).ToListAsync(cancellationToken);

        bool Acted(string action) => action is not ("" or "none");
        return new TodayAiTrader(
            status,
            // The switch, not the last look: just after shadow is switched to live, the last look is still a shadow one.
            aiSettings is null ? rows.FirstOrDefault()?.Mode ?? AiTraderModes.Shadow
                : aiSettings.CurrentValue.AiTraderExecute ? AiTraderModes.Live : AiTraderModes.Shadow,
            rows.Count,
            rows.Count(r => Acted(r.Action)),
            rows.Count(r => Acted(r.Action) && r.Allowed),
            rows.Count(r => !r.Allowed && r.Rule is not ("no-answer" or "unreadable")),
            rows.Count(r => r.Rule is "no-answer" or "unreadable"),
            rows.Take(3).Select(r => new TodayAiTraderDecision(Utc(r.ClockUtc)!.Value, r.Action, r.Underlying, r.Allowed, r.Rule,
                Cut(string.IsNullOrWhiteSpace(r.Reason) ? r.Why : r.Reason, 200)!, AiTraderController.OptionOf(r.PlanJson))).ToList(),
            shadow.Count, shadow.Count(p => p.ExitUtc is null), AiTraderShadowBook.Net(shadow));
    }

    private TodayMarket Market(DateTime now, string exchange, string segment)
    {
        var info = sessions.GetSessionInfo(now, exchange, segment);
        if (!info.IsTradingDay) return new TodayMarket(exchange, "holiday", null, null);
        string state = now < info.SessionOpenUtc ? "pre-open" : now < info.SessionCloseUtc ? "open" : "closed";
        return new TodayMarket(exchange, state, Utc(info.SessionOpenUtc), Utc(info.SessionCloseUtc));
    }

    private async Task<TodayTrading> TradingAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var runs = await history.ListAsync(new LiveRunHistoryFilter(null, null, null, null, today, today, LiveRunHistoryFilter.MaxTake, 0), cancellationToken);
        var recaps = await history.ListAsync(new LiveRunHistoryFilter(null, null, null, null, today, today, LiveRunHistoryFilter.MaxTake, 0, Recaps: true),
            cancellationToken);

        static bool Live(LiveRunSummaryResponse r) => r.IsActive || r.Status is "Running" or "Stopping";
        var accounts = runs
            .GroupBy(r => r.UserName ?? $"user {r.UserId}")
            .Select(g => new TodayAccount(g.Key, Round(g.Sum(r => r.NetPnl)), Round(g.Sum(r => r.GrossPnl)), Round(g.Sum(r => r.Charges)),
                g.Count(Live), g.Count(r => !Live(r)), g.Sum(r => r.OpenPositions)))
            .OrderBy(a => a.Net)
            .ToList();
        var rows = runs
            .OrderBy(r => r.NetPnl)
            .Select(r => new TodayRun(r.RunId, r.UserName ?? string.Empty, r.StrategyName, r.Underlying, Live(r) ? "Running" : r.Status,
                Round(r.NetPnl), r.Trades, Utc(r.StartedUtc), Utc(r.StoppedUtc), r.StopReason))
            .ToList();
        return new TodayTrading(accounts, rows, recaps.Count);
    }

    private async Task<List<TodayAgent>> AgentsAsync(DateTime dayStart, CancellationToken cancellationToken)
    {
        var state = await aiStore.LoadAsync(cancellationToken);
        var built = AiCatalog.Agents.Where(a => a.Built).ToList();
        var keys = built.Select(a => a.Key).ToList();

        var calls = await db.AiCalls.AsNoTracking()
            .Where(c => c.CreatedUtc >= dayStart && keys.Contains(c.AgentKey))
            .GroupBy(c => c.AgentKey)
            .Select(g => new { Key = g.Key, Count = g.Count(), Failed = g.Count(c => c.Outcome == AiCallOutcome.Failed), Last = g.Max(c => c.CreatedUtc) })
            .ToListAsync(cancellationToken);
        var reports = await db.AiReports.AsNoTracking()
            .Where(r => r.UpdatedUtc >= dayStart && keys.Contains(r.AgentKey))
            .Select(r => new { r.Id, r.AgentKey, r.Status, r.Title, r.UpdatedUtc })
            .ToListAsync(cancellationToken);

        var list = new List<TodayAgent>();
        foreach (var a in built)
        {
            var c = calls.FirstOrDefault(x => x.Key == a.Key);
            var mine = reports.Where(r => r.AgentKey == a.Key).ToList();
            var highlights = mine
                .Where(r => r.Status == AiReportStatus.Ok && !string.IsNullOrWhiteSpace(r.Title))
                .OrderByDescending(r => r.UpdatedUtc)
                .Take(4)
                .Select(r => new TodayLink(Cut(r.Title, 160)!, $"/ai/reports?id={r.Id}"))
                .ToList();
            if (a.Key == AiCatalog.DeskAssistant && c is not null)
            {
                highlights.Insert(0, new TodayLink($"{c.Count} question{(c.Count == 1 ? "" : "s")} answered today", "/ai/calls?agent=desk-assistant"));
            }

            list.Add(new TodayAgent(a.Key, a.Name, state.Agent(a.Key)?.Enabled ?? false, c?.Count ?? 0, c?.Failed ?? 0,
                new TodayReportCounts(mine.Count(r => r.Status == AiReportStatus.Ok), mine.Count(r => r.Status == AiReportStatus.Invalid),
                    mine.Count(r => r.Status == AiReportStatus.Failed)),
                c is null ? null : Utc(c.Last), highlights));
        }

        return list;
    }

    private async Task<TodayLearning> LearningAsync(DateOnly today, DateTime dayStart, CancellationToken cancellationToken)
    {
        var memories = await db.AiMemories.AsNoTracking()
            .Where(m => m.Status == AiMemoryStatus.Active || m.DecidedUtc >= dayStart)
            .Select(m => new { m.Id, m.AgentKey, m.Status, m.Text, m.Source, m.DecidedBy, m.DecidedUtc, m.ActivatedUtc })
            .ToListAsync(cancellationToken);
        var learned = memories
            .Where(m => m.Status == AiMemoryStatus.Active && m.ActivatedUtc >= dayStart)
            .OrderByDescending(m => m.ActivatedUtc)
            .Select(m => new TodayLearned(m.Id, AiCatalog.AgentName(m.AgentKey), Cut(m.Text, 200)!, m.Source switch
            {
                AiMemorySource.Owner => "your note",
                AiMemorySource.Feedback => "your correction",
                _ => "tested by the check",
            }))
            .ToList();
        var dropped = memories
            .Where(m => m.Status == AiMemoryStatus.Rejected && m.DecidedUtc >= dayStart)
            .Select(m => new TodayDropped(m.Id, AiCatalog.AgentName(m.AgentKey), Cut(m.Text, 200)!, m.DecidedBy.Replace("check: ", string.Empty)))
            .ToList();

        var checks = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AiCatalog.AssistantCheck && r.SubjectType == AiReportSubject.Check)
            .OrderByDescending(r => r.SubjectId)
            .Take(7)
            .Select(r => new { r.SubjectId, r.DataJson })
            .ToListAsync(cancellationToken);
        var days = checks
            .Select(r => (Date: r.SubjectId, Score: Score(r.DataJson)))
            .Where(x => x.Score is not null)
            .OrderBy(x => x.Date, StringComparer.Ordinal)
            .Select(x => new TodayCheckDay(x.Date, x.Score!.Value.Passed, x.Score.Value.Total))
            .ToList();
        string todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var checkToday = days.FirstOrDefault(d => d.Date == todayKey) is { } t ? new TodayCheck(t.Passed, t.Total) : null;

        // The latest finished exam: the Assistant's score that compares week to week.
        var exam = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AiCatalog.AssistantExam && r.SubjectType == AiReportSubject.Exam && r.Status == AiReportStatus.Ok)
            .OrderByDescending(r => r.CreatedUtc)
            .Select(r => new { r.Id, r.SessionDate, r.DataJson })
            .FirstOrDefaultAsync(cancellationToken);

        return new TodayLearning(memories.Count(m => m.Status == AiMemoryStatus.Active), learned, dropped, checkToday, days)
        {
            LatestExam = exam is null ? null : Exam(exam.Id, exam.SessionDate, exam.DataJson),
        };
    }

    private static TodayExam? Exam(long reportId, DateOnly? day, string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject data || data["all"] is not JsonObject all) return null;
            static double? Share(JsonNode? part) => part?["passK"] is JsonValue v && v.TryGetValue(out double d) ? d : null;
            return new TodayExam(reportId, day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), data["repeats"]?.GetValue<int>() ?? 3,
                all["questions"]?.GetValue<int>() ?? 0, Share(data["practice"]), Share(data["holdout"]));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private async Task<TodaySystem> SystemAsync(CancellationToken cancellationToken)
    {
        var beat = await db.SentinelHeartbeats.AsNoTracking().Select(h => (DateTime?)h.LastCheckUtc).FirstOrDefaultAsync(cancellationToken);
        var checkup = await db.DeskCheckups.AsNoTracking()
            .Where(c => c.CompletedUtc != null && c.Verdict != "")
            .OrderByDescending(c => c.CompletedUtc)
            .Select(c => new { c.Verdict, c.Headline, c.CompletedUtc })
            .FirstOrDefaultAsync(cancellationToken);
        return new TodaySystem(Utc(beat),
            checkup is null ? null : new TodayCheckup(checkup.Verdict, checkup.Headline, Utc(checkup.CompletedUtc)!.Value),
            ReadDeploy(), 0);
    }

    /// <summary>The last deploy that went out, and the newer one waiting for the gate if any.</summary>
    private TodayDeploy? ReadDeploy()
    {
        var path = DeployController.FindHistoryFile(environment.ContentRootPath, out _);
        if (path is null) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var entries = JsonNode.Parse(stream) as JsonArray;
            if (entries is null || entries.Count == 0) return null;
            var done = entries.OfType<JsonObject>().FirstOrDefault(e => e["outcome"]?.GetValue<string>() == "ok");
            var latest = entries.OfType<JsonObject>().First();
            string? waiting = latest != done && latest["outcome"]?.GetValue<string>() == "skipped" ? latest["toCommit"]?.GetValue<string>() : null;
            if (done is null) return null;
            return new TodayDeploy(done["toCommit"]?.GetValue<string>() ?? string.Empty,
                DateTime.TryParse(done["finishedUtc"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at) ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null,
                Cut(done["summary"]?.GetValue<string>(), 200), waiting, waiting is null ? null : Cut(latest["summary"]?.GetValue<string>(), 200));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The decisions on record, newest recorded first.</summary>
    public async Task<List<TodayDecision>> DecisionsAsync(CancellationToken cancellationToken) =>
        (await db.OwnerDecisions.AsNoTracking().OrderByDescending(d => d.Id).Take(DecisionsShown).ToListAsync(cancellationToken))
            .Select(d => new TodayDecision(d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d.Title, d.Decided, d.By, d.Status))
            .ToList();

    /// <summary>Adds a decision at the top of the log, or replaces the one with the same date and title (it moves to the top).</summary>
    public async Task<List<TodayDecision>> RecordAsync(TodayDecision decision, string by, CancellationToken cancellationToken)
    {
        var date = DateOnly.ParseExact(decision.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        string title = decision.Title.ToLowerInvariant();
        var same = await db.OwnerDecisions.Where(d => d.Date == date && d.Title.ToLower() == title).ToListAsync(cancellationToken);
        db.OwnerDecisions.RemoveRange(same);
        if (same.Count > 0) await db.SaveChangesAsync(cancellationToken);

        db.OwnerDecisions.Add(new OwnerDecision
        {
            Date = date, Title = decision.Title, Decided = decision.Decided, By = decision.By, Status = decision.Status,
            RecordedBy = by, RecordedUtc = _time.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(cancellationToken);
        return await DecisionsAsync(cancellationToken);
    }

    private static (int Passed, int Total)? Score(string? json)
    {
        try
        {
            if (JsonNode.Parse(json ?? "{}") is JsonObject o && o["passed"] is JsonValue p && o["total"] is JsonValue t)
            {
                return (p.GetValue<int>(), t.GetValue<int>());
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // An unreadable check report is left out.
        }

        return null;
    }

    private static decimal Round(decimal v) => Math.Round(v, 2);

    private static DateTime? Utc(DateTime? v) => v is null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc);

    private static string? Cut(string? text, int max) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length <= max ? text : text[..(max - 1)] + "…";
}

// ---------- wire shapes (camelCase); the contract is private/notes/2026-10-01-today-page-contract.md ----------

public sealed record TodayResponse(
    string Date, DateTime NowUtc, IReadOnlyList<TodayMarket> Markets, IReadOnlyList<TodayAttention> Attention, TodayTrading Trading,
    IReadOnlyList<TodayAgent> Agents, TodayLearning Learning, TodaySystem System, IReadOnlyList<TodayDecision> Decisions)
{
    /// <summary>The AI Trader's day; null until it has been switched on or has looked.</summary>
    public TodayAiTrader? AiTrader { get; init; }
}

public sealed record TodayMarket(string Exchange, string State, DateTime? OpensUtc, DateTime? ClosesUtc);

/// <summary>
/// The AI Trader today: its looks, the actions it proposed, how many the rules allowed and refused, the looks with
/// no usable answer, and its shadow book (positions opened, still open, net after charges).
/// </summary>
public sealed record TodayAiTrader(string Status, string Mode, int Decisions, int Actions, int Allowed, int Refused, int NoAnswer,
    IReadOnlyList<TodayAiTraderDecision> Latest, int ShadowPositions = 0, int ShadowOpen = 0, decimal ShadowNet = 0m);

/// <summary>One of the AI Trader's latest decisions. <c>Option</c> is the CE or PE its plan named ("Buy NIFTY CE"), else null.</summary>
public sealed record TodayAiTraderDecision(DateTime AtUtc, string Action, string Underlying, bool Allowed, string Rule, string Reason,
    string? Option);

public sealed record TodayAttention(string Level, string Kind, string Title, string? Detail, string? Link, DateTime? AtUtc);

public sealed record TodayTrading(IReadOnlyList<TodayAccount> Accounts, IReadOnlyList<TodayRun> Runs, int RecapsToday);

public sealed record TodayAccount(string UserName, decimal Net, decimal Gross, decimal Charges, int RunsLive, int RunsStopped, int OpenLegs);

public sealed record TodayRun(long RunId, string UserName, string Strategy, string Underlying, string Status, decimal Net, int Trades,
    DateTime? StartedUtc, DateTime? StoppedUtc, string? StopReason);

public sealed record TodayAgent(string Key, string Name, bool On, int CallsToday, int FailedCallsToday, TodayReportCounts ReportsToday,
    DateTime? LastActivityUtc, IReadOnlyList<TodayLink> Highlights);

public sealed record TodayReportCounts(int Ok, int Invalid, int Failed);

public sealed record TodayLink(string Text, string? Link);

public sealed record TodayLearning(int ActiveMemories, IReadOnlyList<TodayLearned> LearnedToday, IReadOnlyList<TodayDropped> DroppedToday,
    TodayCheck? CheckToday, IReadOnlyList<TodayCheckDay> CheckDays)
{
    /// <summary>The latest finished assistant exam; null until one has finished.</summary>
    public TodayExam? LatestExam { get; init; }
}

/// <summary>An exam's score: pass^k on practice and held-out questions (0 to 1; null where none could be scored).</summary>
public sealed record TodayExam(long ReportId, string? Date, int Repeats, int Questions, double? PracticePassK, double? HoldoutPassK);

public sealed record TodayLearned(long Id, string AgentName, string Text, string How);

public sealed record TodayDropped(long Id, string AgentName, string Text, string Why);

public sealed record TodayCheck(int Passed, int Total);

public sealed record TodayCheckDay(string Date, int Passed, int Total);

public sealed record TodaySystem(DateTime? SentinelLastUtc, TodayCheckup? Checkup, TodayDeploy? Deploy, int OpenIncidents);

public sealed record TodayCheckup(string Verdict, string Headline, DateTime Utc);

/// <param name="Waiting">A newer commit the deploy gate is holding, if any.</param>
public sealed record TodayDeploy(string Commit, DateTime? Utc, string? Summary, string? Waiting, string? WaitingWhy);

/// <param name="By">"owner" or "Claude (default)".</param>
/// <param name="Status">decided, default, or open.</param>
public sealed record TodayDecision(string Date, string Title, string Decided, string By, string Status);
