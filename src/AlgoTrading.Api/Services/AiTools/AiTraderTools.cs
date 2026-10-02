using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static AlgoTrading.Api.Services.AiTools.AiToolFormat;

namespace AlgoTrading.Api.Services.AiTools;

// The AI Trader's chat tools: what it reads when the owner talks with it (AiToolNames.AiTraderChat). Each reads the
// rows its console panel reads, through the same builders, so the chat and the page agree; none writes. The look now
// asks a model itself and saves nothing.

/// <summary>How the AI Trader's tools write a decision for the model.</summary>
internal static class AiTraderToolText
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The plan a decision keeps; null for an unreadable answer or an old row.</summary>
    public static AiTraderPlan? Plan(string planJson)
    {
        if (string.IsNullOrWhiteSpace(planJson)) return null;
        try
        {
            var plan = JsonSerializer.Deserialize<AiTraderPlan>(planJson, Json);
            return plan?.Action is { Length: > 0 } ? plan : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A stored JSON text as a node for the model; null when empty or unreadable.</summary>
    public static JsonNode? Node(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What the rules said, in words: allowed, refused, nothing to judge, or no usable answer.</summary>
    public static string Verdict(AiTraderDecision d) =>
        d.Rule is "no-answer" ? "no answer from any model"
        : d.Rule is "unreadable" ? "the answer was not a plan"
        : !d.Allowed ? "refused"
        : d.Action == AiTraderPlan.None ? "nothing to judge"
        : d.Executed ? "allowed and placed"
        : d.Mode == AiTraderModes.Live ? "allowed, not placed"
        : d.Action == AiTraderPlan.Buy ? "allowed, opened in the shadow book"
        : d.Action == AiTraderPlan.Exit ? "allowed, closed in the shadow book"
        : "allowed, recorded only";

    /// <summary>One decision as a list shows it: when, what was proposed, why, and the verdict.</summary>
    public static object Row(AiTraderDecision d)
    {
        var plan = Plan(d.PlanJson);
        return new
        {
            decisionId = d.Id,
            at = Ist(d.ClockUtc),
            mode = d.Mode,
            replay = d.ReplaySessionId,
            action = d.Action.Length > 0 ? d.Action : null,
            underlying = d.Underlying.Length > 0 ? d.Underlying : null,
            option = plan?.Option,
            strike = plan?.Strike,
            lots = plan?.Lots,
            stopLoss = plan?.StopLoss,
            target = plan?.Target,
            positionId = plan?.PositionId,
            strategy = plan?.Strategy,
            reason = Text(d.Reason, 300),
            confidence = d.Confidence,
            verdict = Verdict(d),
            rule = d.Rule.Length > 0 ? d.Rule : null,
            why = Text(d.Why, 300),
            error = Text(d.Error, 200),
        };
    }

    /// <summary>One shadow position, as its panel shows it.</summary>
    public static object Position(AiTraderShadowPositionView p) => new
    {
        positionId = p.Id,
        decisionId = p.DecisionId,
        symbol = p.Symbol,
        underlying = p.Underlying,
        option = p.OptionType,
        strike = p.Strike,
        expiry = p.Expiry,
        lots = p.Lots,
        lotSize = p.LotSize,
        entry = p.EntryIst,
        entryPremium = p.EntryPrice,
        stopLoss = p.StopLoss,
        target = p.Target,
        open = p.Open,
        mark = p.Open ? p.MarkPrice : null,
        markAt = p.Open ? Ist(p.MarkUtc) : null,
        exit = p.ExitIst,
        exitPremium = p.ExitPrice,
        ended = p.ExitReason.Length > 0 ? p.ExitReason : null,
        charges = Rs(p.Charges),
        netPnl = Rs(p.Net),
    };
}

/// <summary><c>get_ai_trader_decisions</c>: the AI Trader's looks, newest first, by day, by replay, or the latest.</summary>
public sealed class AiTraderDecisionsTool(TradingDbContext db, TimeProvider? time = null) : IAiTool
{
    private const int DefaultTake = 30;
    private const int MaxTake = 60;
    private const int MaxCounted = 2000;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.AiTraderDecisions;

    public string Description =>
        "Your decisions (looks), newest first: one IST day's (a day's list holds that day's replays too, each marked with " +
        "its replay id), one replay's, or the latest when neither is given. Each: its id, time, mode (shadow, live, " +
        "replay), what you proposed (action, underlying, option, strike, lots, stop, target), your reason and " +
        "confidence, and the rules' verdict with the rule that decided. Totals over the day or replay. Filter by " +
        "action. get_ai_trader_decision reads one in full, with the brief you read.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("day", AiToolSchema.Text("IST day as yyyy-MM-dd, or 'today' / 'yesterday'."), false),
        ("replay", AiToolSchema.Integer("A market replay's id: only its looks."), false),
        ("action", AiToolSchema.OneOf("Which looks. Default any.", "any", "acted", "buy", "exit", "strategy", "none", "refused", "no-answer"), false),
        ("take", AiToolSchema.Integer($"How many to list, 1 to {MaxTake}. Default {DefaultTake}."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var day = args.Date("day", IstTime.DateOf(now));
        long? replay = args.Long("replay", 1);
        string action = args.String("action", 12)?.ToLowerInvariant() ?? "any";
        int take = args.Int("take", 1, MaxTake) ?? DefaultTake;

        var scope = db.AiTraderDecisions.AsNoTracking();
        if (replay is long r) scope = scope.Where(d => d.ReplaySessionId == r);
        if (day is DateOnly on) scope = scope.Where(d => d.Day == on);

        var listed = action switch
        {
            "any" => scope,
            "acted" => scope.Where(d => d.Action != "" && d.Action != AiTraderPlan.None),
            "buy" or "exit" or "none" => scope.Where(d => d.Action == action),
            "strategy" => scope.Where(d => d.Action == AiTraderPlan.StartStrategy || d.Action == AiTraderPlan.StopStrategy),
            "refused" => scope.Where(d => !d.Allowed && d.Rule != "no-answer" && d.Rule != "unreadable"),
            "no-answer" => scope.Where(d => d.Rule == "no-answer" || d.Rule == "unreadable"),
            _ => throw new AiToolArgumentException("action is any, acted, buy, exit, strategy, none, refused or no-answer."),
        };

        var rows = await listed.OrderByDescending(d => d.ClockUtc).ThenByDescending(d => d.Id).Take(take).ToListAsync(cancellationToken);

        // Totals over the day or the replay, whatever the filter: the latest across all days is not a scope to total.
        object? totals = null;
        object? replays = null;
        if (day is not null || replay is not null)
        {
            var all = await scope.Take(MaxCounted).Select(d => new { d.Action, d.Allowed, d.Rule, d.ReplaySessionId, d.Mode }).ToListAsync(cancellationToken);
            bool NoAnswer(string rule) => rule is "no-answer" or "unreadable";
            totals = new
            {
                looks = all.Count,
                acted = all.Count(d => d.Action is not ("" or AiTraderPlan.None)),
                allowed = all.Count(d => d.Action is not ("" or AiTraderPlan.None) && d.Allowed),
                refused = all.Count(d => !d.Allowed && !NoAnswer(d.Rule)),
                noAnswer = all.Count(d => NoAnswer(d.Rule)),
            };
            replays = all.GroupBy(d => d.ReplaySessionId)
                .Select(g => new { replay = g.Key, mode = g.First().Mode, looks = g.Count() })
                .OrderBy(g => g.replay ?? 0)
                .ToList();
        }

        var data = new
        {
            day = day?.ToString("yyyy-MM-dd"),
            replay,
            action,
            note = day is null && replay is null ? "The latest looks across every day and replay." : null,
            decisions = rows.Select(AiTraderToolText.Row).ToList(),
            totals,
            byReplay = replays,
        };

        string what = replay is long id ? $"replay {id}" : day is DateOnly dd ? $"{dd:dd MMM}" : "latest";
        return new AiToolOutput(data, rows.Count == 0 ? null : rows.Max(d => d.CreatedUtc), rows.Count, $"{rows.Count} decisions ({what})");
    }
}

/// <summary><c>get_ai_trader_decision</c>: one of the AI Trader's looks in full, with the brief it read and the memories its call was given.</summary>
public sealed class AiTraderDecisionTool(TradingDbContext db) : IAiTool
{
    private const int MaxBrief = 8000;

    public string Name => AiToolNames.AiTraderDecision;

    public string Description =>
        "One of your decisions in full, by its id: when and in which mode, the brief you read then (the market, the " +
        "chains, your book, your last looks; cut to fit), your plan and reason, the rules' verdict, what was done (the " +
        "contract, a shadow position opened or closed), the lessons and notes that call was given, and how a shadow " +
        "position it opened ended. Explain a decision from this, not from what happened afterwards.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("decisionId", AiToolSchema.Integer("The decision's id, from get_ai_trader_decisions."), true),
        ("brief", AiToolSchema.Flag("False leaves the brief out. Default true."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        long id = args.Long("decisionId", 1) ?? throw new AiToolArgumentException("decisionId is required; get_ai_trader_decisions lists them.");
        bool withBrief = args.Bool("brief") ?? true;
        var d = await db.AiTraderDecisions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new AiToolArgumentException($"No decision {id}; get_ai_trader_decisions lists them.");

        string? memoryJson = d.CallId is long callId
            ? await db.AiCalls.AsNoTracking().Where(c => c.Id == callId).Select(c => c.MemoryIdsJson).FirstOrDefaultAsync(cancellationToken)
            : null;
        var memoryIds = AiGateway.MemoryIds(memoryJson);
        var memories = memoryIds.Count == 0
            ? []
            : await db.AiMemories.AsNoTracking().Where(m => memoryIds.Contains(m.Id)).ToListAsync(cancellationToken);
        var positions = await db.AiTraderShadowPositions.AsNoTracking()
            .Where(p => p.DecisionId == id).OrderBy(p => p.EntryUtc).ToListAsync(cancellationToken);

        var data = new
        {
            decision = AiTraderToolText.Row(d),
            model = d.Model.Length > 0 ? d.Model : null,
            callId = d.CallId,
            plan = AiTraderToolText.Node(d.PlanJson),
            result = AiTraderToolText.Node(d.ResultJson),
            memoriesGiven = memoryIds
                .Select(mid => memories.FirstOrDefault(m => m.Id == mid))
                .Where(m => m is not null)
                .Select(m => new { memoryId = m!.Id, kind = m.Kind, statusNow = m.Status, text = Text(m.Text, 300) })
                .ToList(),
            shadowPositions = positions.Select(p => AiTraderToolText.Position(AiTraderController.View(p))).ToList(),
            brief = withBrief ? Text(d.Brief, MaxBrief) : null,
            briefChars = d.Brief.Length,
        };

        return new AiToolOutput(data, d.CreatedUtc, 1,
            $"Decision {id}: {(d.Action.Length > 0 ? d.Action : "no plan")} {d.Underlying}, {AiTraderToolText.Verdict(d)}".Replace("  ", " "));
    }
}

/// <summary><c>get_ai_trader_book</c>: the AI Trader's shadow book for a day or a replay, net after charges.</summary>
public sealed class AiTraderBookTool(TradingDbContext db, TimeProvider? time = null) : IAiTool
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.AiTraderBook;

    public string Description =>
        "Your shadow book (the buys the rules allowed, kept by code as if placed): one IST day's live shadow positions " +
        "(default today) or one replay's. Each position: the decision that opened it, the contract, lots, entry time " +
        "and premium (the ask), stop and target, the mark or the exit, how it ended (stop, target, exit: your own, " +
        "close: squared off at 15:30, replay-ended) and its net after charges; then the totals.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("day", AiToolSchema.Text("IST day as yyyy-MM-dd, or 'today' / 'yesterday'. Default today."), false),
        ("replay", AiToolSchema.Integer("A market replay's id: its own shadow book."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var today = IstTime.DateOf(now);
        var day = args.Date("day", today) ?? today;
        long? replay = args.Long("replay", 1);

        var query = replay is long id
            ? db.AiTraderShadowPositions.Where(p => p.ReplaySessionId == id)
            : db.AiTraderShadowPositions.Where(p => p.ReplaySessionId == null && p.Day == day);
        var positions = await query.AsNoTracking().OrderBy(p => p.EntryUtc).ThenBy(p => p.Id).ToListAsync(cancellationToken);
        decimal net = AiTraderShadowBook.Net(positions);

        var data = new
        {
            day = replay is null ? day.ToString("yyyy-MM-dd") : null,
            replay,
            positions = positions.Select(p => AiTraderToolText.Position(AiTraderController.View(p))).ToList(),
            totals = new
            {
                positions = positions.Count,
                open = positions.Count(p => p.ExitUtc is null),
                netPnl = Rs(net),
                charges = Rs(positions.Sum(p => p.Charges)),
                note = positions.Any(p => p.ExitUtc is null) ? "An open position's net is as if sold at its mark." : null,
            },
        };

        string what = replay is long r ? $"replay {r}" : $"{day:dd MMM}";
        return new AiToolOutput(data, positions.Count == 0 ? null : positions.Max(p => p.MarkUtc ?? p.ExitUtc ?? p.EntryUtc), positions.Count,
            $"{positions.Count} shadow positions ({what}), net ₹{Rs(net):N2}");
    }
}

/// <summary><c>get_ai_trader_scoreboard</c>: the AI Trader against the baseline rule, as its panel shows it.</summary>
public sealed class AiTraderScoreboardTool(TradingDbContext db) : IAiTool
{
    public string Name => AiToolNames.AiTraderScoreboard;

    public string Description =>
        "You against a fixed baseline rule anyone could follow: each replay and live shadow day, newest first, with " +
        "your looks, positions and net after charges beside the rule's trade and net that day (doing nothing scores " +
        "₹0), and the totals over every full day (looks from 09:30 or earlier to 14:30 or later) whose baseline is " +
        "scored. The rule is described in the answer.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("take", AiToolSchema.Integer("How many rows to list, 1 to 40. Default 15. The totals cover every row."), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        int take = args.Int("take", 1, 40) ?? 15;
        var board = await AiTraderController.ScoreboardAsync(db, take, cancellationToken);
        var t = board.Totals;
        var data = new
        {
            rule = board.Rule,
            ruleText = board.RuleText,
            totals = new
            {
                fullScoredRows = t.Days,
                yourNet = Rs(t.AiNet),
                baselineNet = Rs(t.BaselineNet),
                rowsYouBeatTheRule = t.AiBeatBaseline,
                rowsYouMadeMoney = t.AiPositiveDays,
                rowsTheRuleMadeMoney = t.BaselinePositiveDays,
                yourTrades = t.Trades,
                yourCharges = Rs(t.Charges),
                note = "A row is a replay or a live shadow day; a day replayed twice is two rows against the same rule result.",
            },
            rows = board.Rows.Select(r => new
            {
                kind = r.Kind,
                replay = r.ReplaySessionId,
                day = r.Day,
                looksFrom = r.FirstIst,
                looksTo = r.LastIst,
                full = r.Full,
                looks = r.Looks,
                actions = r.Actions,
                noAnswer = r.NoAnswer,
                positions = r.Positions,
                open = r.Open,
                netPnl = Rs(r.Net),
                charges = Rs(r.Charges),
                baseline = r.Baseline is { } b
                    ? new
                    {
                        side = b.OptionType.Length > 0 ? b.OptionType : "no trade",
                        symbol = b.Symbol.Length > 0 ? b.Symbol : null,
                        entry = b.EntryIst,
                        entryPremium = b.EntryPrice,
                        exit = b.ExitIst,
                        exitPremium = b.ExitPrice,
                        ended = b.ExitReason.Length > 0 ? b.ExitReason : null,
                        netPnl = Rs(b.Net),
                        note = Text(b.Note, 200),
                    }
                    : null,
                youLessBaseline = Rs(r.VsBaseline),
            }).ToList(),
            rowsListed = board.Rows.Count,
            rowsTotal = board.RowsTotal,
        };

        return new AiToolOutput(data, null, board.Rows.Count,
            $"{t.Days} full scored rows: you ₹{Rs(t.AiNet):N2}, the rule ₹{Rs(t.BaselineNet):N2}");
    }
}

/// <summary>
/// <c>get_ai_trader_lessons</c>: the AI Trader's lessons with their day and evidence, as its panel shows them, and the
/// memories its next look reads (lessons from earlier days only).
/// </summary>
public sealed class AiTraderLessonsTool(
    TradingDbContext db, IOptionsMonitor<AiSettings> settings, TimeProvider? time = null, AiTraderLessonState? lessonState = null) : IAiTool
{
    private const int MaxLessons = 40;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.AiTraderLessons;

    public string Description =>
        "Your lessons, newest first: each with its status (active: you read it; proposed: waiting for or under its " +
        "test; rejected: its test or the owner dropped it; retired), the day it was learned from and the decisions it " +
        "rests on, and once tested its evidence (your net on past looks without it and with it, the looks it helped " +
        "and hurt). Also readNow: the lessons and the owner's notes and corrections your next look reads (a lesson " +
        "only from a day before today), and the lesson under test.";

    public JsonObject Parameters => AiToolSchema.Object(
        ("status", AiToolSchema.OneOf("Which lessons. Default all.", "all", "active", "proposed", "rejected", "retired"), false));

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        string status = args.String("status", 10)?.ToLowerInvariant() ?? "all";
        if (status != "all" && !AiMemoryStatus.All.Contains(status)) throw new AiToolArgumentException("status is all, active, proposed, rejected or retired.");

        var now = _time.GetUtcNow().UtcDateTime;
        var view = await AiTraderController.LessonsAsync(db, settings.CurrentValue, lessonState, cancellationToken);
        var readNow = await new AiTraderMemory(db, settings).ForAsync(now, count: false, now, cancellationToken);
        var lessons = view.Lessons.Where(l => status == "all" || l.Status == status).Take(MaxLessons).ToList();

        var data = new
        {
            learning = view.Enabled,
            maxActive = view.MaxActive,
            counts = view.Counts,
            underTest = view.Testing is { } t ? new { lessonId = t.LessonId, looksAsked = t.Done, of = t.Of } : null,
            readNow = readNow.Select(m => new { memoryId = m.Id, kind = m.Kind, text = Text(m.Text, 300) }).ToList(),
            lessons = lessons.Select(l => new
            {
                lessonId = l.Id,
                text = Text(l.Text, 300),
                status = l.Status,
                learnedFrom = l.SourceDay,
                subject = l.Subject,
                decisionIds = l.DecisionIds,
                activated = Ist(l.ActivatedUtc),
                retired = Ist(l.RetiredUtc),
                decidedBy = l.DecidedBy.Length > 0 ? l.DecidedBy : null,
                uses = l.Uses,
                evidence = l.Evidence is { } e
                    ? new
                    {
                        looks = e.Points,
                        withoutIt = Rs(e.ControlNet),
                        withIt = Rs(e.TreatmentNet),
                        gain = Rs(e.Gain),
                        helped = e.Helped,
                        hurt = e.Hurt,
                        passed = e.Passed,
                        verdict = Text(e.Verdict, 200),
                    }
                    : null,
            }).ToList(),
            listed = lessons.Count,
        };

        return new AiToolOutput(data, now, lessons.Count, $"{lessons.Count} lessons ({status}); a look now reads {readNow.Count} memories");
    }
}

/// <summary>
/// <c>ai_trader_look_now</c>: "what would you do now". A look built and judged as now that saves nothing
/// (<see cref="AiTraderAgent.PreviewAsync"/>): one model call, made as the person who asked.
/// </summary>
/// <remarks>
/// It runs in a scope of its own: the look asks the gateway itself, and the chat that called the tool holds the
/// request's gateway and database context. The asker's rate limits pay for it (the caller's name), so the AI
/// Trader's own looks are never turned away for it. Up to <see cref="Timeout"/>: a model can take a minute to answer.
/// </remarks>
public sealed class AiTraderLookNowTool(IServiceScopeFactory scopes, TimeProvider? time = null) : IAiTool
{
    private const int BriefBlock = 900;
    private const int BriefTotal = 7000;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string Name => AiToolNames.AiTraderLookNow;

    public TimeSpan? Timeout => TimeSpan.FromMinutes(3);

    public string Description =>
        "What you would do now: builds your market brief and your book as of now, asks your own decision prompt once " +
        "(with the lessons and notes a look now reads) and judges the plan with the rules. Saves nothing and opens " +
        "nothing: no decision, no shadow position. Not while a market replay you decide in is playing. Costs one " +
        "model call and can take a minute. Answers a summary of the brief, your plan and reason, and the rules' verdict.";

    public JsonObject Parameters => AiToolSchema.Object();

    public async Task<AiToolOutput> RunAsync(AiToolArgs args, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        string by = args.Caller?.RequestedBy is { Length: > 0 } asker ? asker : AiCatalog.AiTrader;

        await using var scope = scopes.CreateAsyncScope();
        var agent = scope.ServiceProvider.GetServices<IAiScheduledAgent>().OfType<AiTraderAgent>().FirstOrDefault()
            ?? throw new InvalidOperationException("The AI Trader is not on this build.");
        var p = await agent.PreviewAsync(now, by, args.Caller?.UserId, cancellationToken);
        if (p.NotTakenBecause is string why)
        {
            return new AiToolOutput(new { taken = false, note = why }, now, 0, "No look: a replay it decides in is playing");
        }

        var plan = p.Plan;
        var data = new
        {
            taken = true,
            at = Ist(p.ClockUtc),
            mode = p.Mode,
            saved = "Nothing: no decision row, no shadow position, nothing placed.",
            plan = plan is null
                ? null
                : new
                {
                    action = plan.Action,
                    underlying = plan.Underlying,
                    option = plan.Option,
                    strike = plan.Strike,
                    lots = plan.Lots,
                    stopLoss = plan.StopLoss,
                    target = plan.Target,
                    positionId = plan.PositionId,
                    strategy = plan.Strategy,
                    runId = plan.RunId,
                    reason = Text(plan.Reason, 500),
                    confidence = plan.Confidence,
                },
            contract = p.Contract is { } c
                ? new { symbol = c.Symbol, strike = c.Strike, expiry = c.Expiry.ToString("yyyy-MM-dd"), ask = c.Ask, lotSize = c.LotSize }
                : null,
            verdict = p.Verdict is { } v ? new { allowed = v.Allowed, rule = v.Rule, why = Text(v.Why, 400) } : null,
            answer = Text(p.Answer, 1000),
            error = Text(p.Error, 400),
            memoriesRead = p.Memories.Select(m => new { memoryId = m.Id, kind = m.Kind, text = Text(m.Text, 200) }).ToList(),
            briefSummary = BriefSummary(p.Brief),
            briefChars = p.Brief.Length,
            callId = p.CallId,
            model = p.Model.Length > 0 ? p.Model : null,
        };

        string summary = plan is null
            ? "No plan: " + (p.Error ?? "no answer")
            : $"{plan.Action} {plan.Underlying} {plan.Option}".TrimEnd() + (p.Verdict is { } verdict ? (verdict.Allowed ? " → allowed" : $" → refused ({verdict.Rule})") : string.Empty);
        return new AiToolOutput(data, now, 1, Text(summary, 160) ?? summary);
    }

    /// <summary>
    /// The brief, shortened section by section: each block (the indices, the base rates, the chains, the forecasts,
    /// the context, the news, the stop floors, the book, the last looks) keeps its first lines, so every part is seen
    /// and none crowds out the rest. The closing instruction to the model is left out.
    /// </summary>
    public static string BriefSummary(string brief, int perBlock = BriefBlock, int total = BriefTotal)
    {
        var text = new StringBuilder();
        foreach (string raw in brief.Replace("\r", string.Empty).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string block = string.Join('\n', raw.Split('\n').Where(l => !l.StartsWith("Decide now", StringComparison.Ordinal))).Trim();
            if (block.Length == 0) continue;
            if (block.Length > perBlock) block = block[..(perBlock - 1)] + "…";
            if (text.Length + block.Length + 2 > total)
            {
                text.Append("\n\n[the rest of the brief is left out]");
                break;
            }

            if (text.Length > 0) text.Append("\n\n");
            text.Append(block);
        }

        return Text(text.ToString(), total + 64) ?? string.Empty;
    }
}
