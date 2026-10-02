using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services.AgentMemory;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Api.Services.Replay;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>A finished day of the AI Trader's decisions: one replay (<c>replay:12</c>) or one live day (<c>day:2026-10-05</c>).</summary>
public sealed record AiTraderDaySubject(string Id, DateOnly Day, long? ReplaySessionId)
{
    public static AiTraderDaySubject Of(long? replaySessionId, DateOnly day) => replaySessionId is long r
        ? new($"replay:{r.ToString(CultureInfo.InvariantCulture)}", day, r)
        : new($"day:{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", day, null);

    /// <summary>"Replay #12 of Wed 16 Sep", "Live day Mon 5 Oct".</summary>
    public string Label => ReplaySessionId is long r
        ? $"Replay #{r.ToString(CultureInfo.InvariantCulture)} of {Day.ToString("ddd d MMM", CultureInfo.InvariantCulture)}"
        : $"Live day {Day.ToString("ddd d MMM", CultureInfo.InvariantCulture)}";
}

/// <summary>A lesson the reflection kept, and the day's decisions it rests on.</summary>
public sealed record ReflectedLesson(string Text, IReadOnlyList<long> DecisionIds);

/// <summary>A lesson the reflection's checks turned away, and why.</summary>
public sealed record DroppedLesson(string Text, string Why);

/// <summary>The Judge's answer read: the lessons kept, those dropped, and its summary of the day.</summary>
public sealed record ReflectionRead(IReadOnlyList<ReflectedLesson> Kept, IReadOnlyList<DroppedLesson> Dropped, string Summary);

/// <summary>
/// The AI Trader's reflection (owner, 2 Oct): after each finished day of its decisions, one Judge call reads the day
/// and proposes at most three lessons for its later days.
/// </summary>
/// <remarks>
/// <para>
/// A day is finished when it can no longer change: a replay once it has ended and its shadow book is closed (the
/// minute check closes it within a minute); a live day from 15:45 IST, once its 15:30 square-off has settled and the
/// baseline rule can score it. The Judge reads, in code-written words, every look of the day (time, what it
/// proposed, the verdict, its reason), the shadow positions with entry, exit, how each ended and the net after
/// charges, and what the baseline rule did that day.
/// </para>
/// <para>
/// One reflection per finished replay and per live day, kept as a report (agent <c>ai-trader-reflect</c>, subject
/// <c>replay:12</c> or <c>day:2026-10-05</c>, its day as the session date): a day reflected is never reflected
/// again. A day with no readable decision is skipped. A failed or unreadable answer is tried again after 15 minutes,
/// three times in all; one turned away for capacity is not counted.
/// </para>
/// <para>
/// A lesson must be general: at most 300 characters, no date, no price or index level, no contract, resting on at
/// least one of the day's decisions, and not one already kept. Each one kept is proposed (<c>ai_memories</c>, agent
/// <c>ai-trader</c>, kind lesson, source check, via check, its source report this one) and read by no one until its
/// test passes (<see cref="AiTraderLessonCheck"/>).
/// </remarks>
public sealed class AiTraderReflection(
    TradingDbContext db,
    AiGateway gateway,
    AiReportWriter reports,
    AiMemoryService memory,
    AiTraderBaselineScorer baselines,
    IReplaySessions replays,
    ILogger<AiTraderReflection> logger)
{
    /// <summary>Lessons one day may give at most.</summary>
    public const int MaxLessons = 3;

    /// <summary>The longest lesson; a longer one is dropped, not cut (a cut rule says something else).</summary>
    public const int MaxLessonChars = 300;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The finished days not reflected on yet (or with a failed try due again), oldest first: replays that have
    /// ended with their shadow book closed, and live days that are over (today from 15:45 IST) with none left open.
    /// </summary>
    public async Task<IReadOnlyList<AiTraderDaySubject>> DueAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var today = IstTime.DateOf(nowUtc);
        var lastLiveDay = baselines.IsOver(today, nowUtc) ? today : today.AddDays(-1);
        var replay = await replays.LoadAsync(cancellationToken);
        long? playing = replay is not null && MarketReplayService.IsActive(replay.State) ? replay.Id : null;

        var days = await db.AiTraderDecisions.AsNoTracking()
            .Where(d => d.Rule != "no-answer" && d.Rule != "unreadable")
            .Where(d => d.ReplaySessionId != null || d.Day <= lastLiveDay)
            .Select(d => new { d.ReplaySessionId, d.Day })
            .Distinct()
            .ToListAsync(cancellationToken);
        var open = (await db.AiTraderShadowPositions.AsNoTracking()
                .Where(p => p.ExitUtc == null)
                .Select(p => new { p.ReplaySessionId, p.Day })
                .ToListAsync(cancellationToken))
            .Select(p => p.ReplaySessionId is long r ? AiTraderDaySubject.Of(r, p.Day).Id : AiTraderDaySubject.Of(null, p.Day).Id)
            .ToHashSet(StringComparer.Ordinal);

        var subjects = days
            .Where(d => d.ReplaySessionId is null || d.ReplaySessionId != playing)
            .Select(d => AiTraderDaySubject.Of(d.ReplaySessionId, d.Day))
            .Where(s => !open.Contains(s.Id))
            .GroupBy(s => s.Id).Select(g => g.First())
            .ToList();
        if (subjects.Count == 0) return [];

        var due = await reports.DueAsync(AiCatalog.AiTraderReflect, AiReportSubject.Check, subjects.Select(s => s.Id).ToList(), cancellationToken);
        return subjects.Where(s => due.Contains(s.Id)).OrderBy(s => s.Day).ThenBy(s => s.ReplaySessionId ?? long.MaxValue).ToList();
    }

    /// <summary>
    /// Reflects on one finished day: one Judge call, the report, and each lesson kept proposed. Null when the day has no
    /// readable decision.
    /// </summary>
    public async Task<AiReport?> ReflectAsync(AiTraderDaySubject subject, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var decisions = await (subject.ReplaySessionId is long r
                ? db.AiTraderDecisions.Where(d => d.ReplaySessionId == r)
                : db.AiTraderDecisions.Where(d => d.ReplaySessionId == null && d.Day == subject.Day))
            .AsNoTracking().OrderBy(d => d.ClockUtc).ThenBy(d => d.Id).ToListAsync(cancellationToken);
        if (!decisions.Any(d => d.Rule is not ("no-answer" or "unreadable"))) return null;

        var positions = await (subject.ReplaySessionId is long rp
                ? db.AiTraderShadowPositions.Where(p => p.ReplaySessionId == rp)
                : db.AiTraderShadowPositions.Where(p => p.ReplaySessionId == null && p.Day == subject.Day))
            .AsNoTracking().OrderBy(p => p.EntryUtc).ThenBy(p => p.Id).ToListAsync(cancellationToken);

        AiTraderBaseline? baseline = null;
        try
        {
            baseline = await baselines.ForDayAsync(subject.Day, nowUtc, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The day is still worth reading without it; the text says it is not known.
            logger.LogInformation("AI Trader reflection on {Subject}: the baseline could not be scored ({Error}); it reads the day without it",
                subject.Id, ex.GetType().Name);
        }

        string text = DayText(subject, decisions, positions, baseline);
        var judge = (await new AiSettingsStore(db).LoadAsync(cancellationToken)).Tier("judge").Chain;
        var result = await gateway.AskAsync(new AiAskInput(
            AiCatalog.AiTraderReflect, null, [new AiMessage("user", text)], AiCatalog.AiTraderReflectPrompt, 6000, 0.2,
            $"reflect-{subject.Id}", "check", AiCatalog.AiTraderReflect, null, judge), NullAiStreamSink.Instance, cancellationToken);

        decimal net = AiTraderShadowBook.Net(positions);
        string head = $"{subject.Label}: {decisions.Count} looks, {positions.Count} {(positions.Count == 1 ? "trade" : "trades")}, net {Rupees(net)}";
        if (result.Outcome != AiCallOutcome.Ok)
        {
            logger.LogInformation("AI Trader reflection on {Subject}: no answer ({Error}); tried again later", subject.Id, result.Error);
            return AiGateway.IsCapacityRefusal(result)
                ? await reports.SaveTurnedAwayAsync(AiCatalog.AiTraderReflect, AiReportSubject.Check, subject.Id, subject.Day, result,
                    $"{head}: no answer", $"Turned away for capacity, not counted as a try: {result.Error}", cancellationToken)
                : await reports.SaveAsync(AiCatalog.AiTraderReflect, AiReportSubject.Check, subject.Id, subject.Day, AiReportStatus.Failed, result,
                    $"{head}: no answer", string.Empty, "{}", result.Error, cancellationToken);
        }

        var ids = decisions.Select(d => d.Id).ToHashSet();
        var read = Read(result.Text, ids);
        if (read is null)
        {
            // Tried again like a failure: a day not read is a day not learned from.
            return await reports.SaveAsync(AiCatalog.AiTraderReflect, AiReportSubject.Check, subject.Id, subject.Day, AiReportStatus.Failed, result,
                $"{head}: the answer was not the JSON asked for", Cut(result.Text, 4000), "{}",
                "The answer was not the JSON object asked for, with a lessons list.", cancellationToken);
        }

        // A lesson already kept (any status: tested, retired or turned down) is not proposed again.
        var known = (await db.AiMemories.AsNoTracking()
                .Where(m => m.AgentKey == AiCatalog.AiTrader && m.Kind == AiMemoryKind.Lesson)
                .Select(m => m.Text).ToListAsync(cancellationToken))
            .Select(Fold).ToHashSet(StringComparer.Ordinal);
        var kept = new List<ReflectedLesson>();
        var dropped = read.Dropped.ToList();
        foreach (var lesson in read.Kept)
        {
            if (known.Contains(Fold(lesson.Text))) dropped.Add(new DroppedLesson(lesson.Text, "already a lesson"));
            else kept.Add(lesson);
        }

        var report = await reports.SaveAsync(AiCatalog.AiTraderReflect, AiReportSubject.Check, subject.Id, subject.Day, AiReportStatus.Ok, result,
            Title(head, kept.Count), string.Empty, "{}", string.Empty, cancellationToken);

        var proposed = new List<(AiMemory Memory, ReflectedLesson Lesson)>();
        foreach (var lesson in kept)
        {
            string context = $"{subject.Label}: decisions {string.Join(", ", lesson.DecisionIds.Select(i => i.ToString(CultureInfo.InvariantCulture)))}";
            try
            {
                var saved = await memory.ProposeLessonAsync(AiCatalog.AiTrader, lesson.Text, context, AiCatalog.AiTraderReflect,
                    result.CallId, report.Id, cancellationToken);
                proposed.Add((saved, lesson));
            }
            catch (AiMemoryException ex)
            {
                dropped.Add(new DroppedLesson(lesson.Text, ex.Message));
            }
        }

        report.Title = Cut(Title(head, proposed.Count), 300);
        report.Body = Body(subject, decisions.Count, positions, net, baseline, read.Summary, proposed, dropped);
        report.DataJson = JsonSerializer.Serialize(new
        {
            subject = subject.Id,
            day = subject.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            replaySessionId = subject.ReplaySessionId,
            looks = decisions.Count,
            trades = positions.Count,
            net,
            baselineNet = baseline?.NetPnl,
            summary = read.Summary,
            lessons = proposed.Select(p => new { memoryId = p.Memory.Id, text = p.Memory.Text, decisionIds = p.Lesson.DecisionIds }),
            dropped = dropped.Select(d => new { text = d.Text, why = d.Why }),
        }, Json);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("AI Trader reflection on {Subject}: {Kept} lessons proposed, {Dropped} dropped", subject.Id, proposed.Count, dropped.Count);
        return report;
    }

    private static string Title(string head, int lessons) =>
        $"{head}; {(lessons == 0 ? "no lesson" : lessons == 1 ? "1 lesson proposed" : $"{lessons} lessons proposed")}";

    /// <summary>
    /// The day as the Judge reads it: every look in order (its number, time, what it proposed, the verdict, its
    /// reason), the shadow positions with how each ended and its net after charges, and the baseline rule's trade.
    /// Public for tests.
    /// </summary>
    public static string DayText(AiTraderDaySubject subject, IReadOnlyList<AiTraderDecision> decisions,
        IReadOnlyList<AiTraderShadowPosition> positions, AiTraderBaseline? baseline)
    {
        var t = new StringBuilder();
        string kind = subject.ReplaySessionId is long r
            ? $"a recorded day replayed (replay #{r.ToString(CultureInfo.InvariantCulture)}); nothing was placed"
            : decisions.Any(d => d.Mode == AiTraderModes.Live) ? "live" : "live, in shadow mode: nothing was placed";
        t.Append("DAY: ").Append(subject.Day.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)).Append(" (").Append(kind).Append(")\n");
        int proposed = decisions.Count(d => d.Action is not ("" or AiTraderPlan.None));
        t.Append(CultureInfo.InvariantCulture, $"Looks: {decisions.Count}; actions proposed {proposed}, allowed {decisions.Count(d => d.Allowed && d.Action is not ("" or AiTraderPlan.None))}, ")
            .Append(CultureInfo.InvariantCulture, $"refused {decisions.Count(d => !d.Allowed && d.Rule is not ("no-answer" or "unreadable"))}, no usable answer {decisions.Count(d => d.Rule is "no-answer" or "unreadable")}.\n\n");

        t.Append("ITS LOOKS (IST, oldest first; #number, then what it proposed → the rules' verdict, then its reason)\n");
        foreach (var d in decisions) t.Append(DecisionLine(d)).Append('\n');

        t.Append("\nITS SHADOW POSITIONS (opened by its allowed buys; bought at the ask, checked every minute at the bid)\n");
        if (positions.Count == 0) t.Append("None: it opened no position.\n");
        foreach (var p in positions) t.Append(PositionLine(p)).Append('\n');
        if (positions.Count > 0)
        {
            t.Append(CultureInfo.InvariantCulture,
                $"The day: {positions.Count} {(positions.Count == 1 ? "position" : "positions")}, net {Rupees(AiTraderShadowBook.Net(positions))} after {Rupees(positions.Sum(p => p.Charges))} charges.\n");
        }

        t.Append("\nTHE BASELINE RULE (").Append(AiTraderBaselineScorer.TrendRule).Append("): ").Append(AiTraderController.BaselineRuleText).Append('\n');
        t.Append("That day: ").Append(BaselineLine(baseline)).Append('\n');
        t.Append("\nWrite at most 3 lessons for its later days, as asked: one JSON object.");
        return t.ToString();
    }

    private static string DecisionLine(AiTraderDecision d)
    {
        string at = IstTime.ToIst(d.ClockUtc).ToString("HH:mm", CultureInfo.InvariantCulture);
        string id = "#" + d.Id.ToString(CultureInfo.InvariantCulture);
        if (d.Rule is "no-answer" or "unreadable") return $"{id} {at} no usable answer.";
        string what = d.Action == AiTraderPlan.Buy ? AiTraderAgent.BuyText(d.PlanJson)
            : d.Action == AiTraderPlan.Exit && PositionIdOf(d.PlanJson) is long pid ? $"exit position P{pid.ToString(CultureInfo.InvariantCulture)}"
            : $"{d.Action} {d.Underlying}".TrimEnd();
        string verdict = !d.Allowed ? $"refused ({d.Rule}): {Cut(d.Why, 200)}"
            : d.Action == AiTraderPlan.None ? "nothing to judge"
            : ShadowOf(d.ResultJson) is long sp ? $"allowed; position P{sp.ToString(CultureInfo.InvariantCulture)}"
            : "allowed";
        string reason = d.Reason.Length > 0 ? $" Reason: \"{Cut(d.Reason, 300)}\"" : string.Empty;
        string confidence = d.Confidence is double c ? string.Create(CultureInfo.InvariantCulture, $" (confidence {c:0.##})") : string.Empty;
        return $"{id} {at} {what} → {verdict}.{reason}{confidence}";
    }

    private static string PositionLine(AiTraderShadowPosition p)
    {
        string inAt = IstTime.ToIst(p.EntryUtc).ToString("HH:mm", CultureInfo.InvariantCulture);
        string outAt = p.ExitUtc is DateTime x ? IstTime.ToIst(x).ToString("HH:mm", CultureInfo.InvariantCulture) : "still open";
        string ended = p.ExitReason switch
        {
            AiTraderShadowBook.Stopped => "its stop",
            AiTraderShadowBook.TargetHit => "its target",
            AiTraderShadowBook.ExitedByIt => "its own exit",
            AiTraderShadowBook.SessionClose => "the 15:30 close",
            AiTraderShadowBook.ReplayEnded => "the replay's end",
            _ => "not yet",
        };
        decimal net = p.ExitUtc is null ? AiTraderShadowBook.Net([p]) : p.NetPnl ?? 0m;
        return string.Create(CultureInfo.InvariantCulture,
            $"P{p.Id} {p.Underlying} {p.Strike:0.##} {p.OptionType}, {p.Lots} lot(s) of {p.LotSize}: in {inAt} at {p.EntryPrice:0.##}, stop {p.StopLoss:0.##}, target {p.Target:0.##}; out {outAt} at {(p.ExitPrice ?? p.MarkPrice ?? p.EntryPrice):0.##} by {ended}; net {Rupees(net)} after {Rupees(p.Charges)} charges. Opened by #{p.DecisionId}.");
    }

    private static string BaselineLine(AiTraderBaseline? b)
    {
        if (b is null) return "not scored (no recorded data could be read for it).";
        if (string.IsNullOrEmpty(b.OptionType)) return $"no trade: {b.Note}";
        string inAt = b.EntryUtc is DateTime e ? IstTime.ToIst(e).ToString("HH:mm", CultureInfo.InvariantCulture) : "?";
        string outAt = b.ExitUtc is DateTime x ? IstTime.ToIst(x).ToString("HH:mm", CultureInfo.InvariantCulture) : "?";
        return string.Create(CultureInfo.InvariantCulture,
            $"bought the at-the-money {b.OptionType} at {inAt} at {b.EntryPrice:0.##}, out at {outAt} at {b.ExitPrice:0.##} by its {b.ExitReason}; net {Rupees(b.NetPnl)} after charges.");
    }

    private static string Body(AiTraderDaySubject subject, int looks, IReadOnlyList<AiTraderShadowPosition> positions, decimal net,
        AiTraderBaseline? baseline, string summary, IReadOnlyList<(AiMemory Memory, ReflectedLesson Lesson)> proposed, IReadOnlyList<DroppedLesson> dropped)
    {
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"**{subject.Label}**: {looks} looks, {positions.Count} {(positions.Count == 1 ? "trade" : "trades")}, net {Rupees(net)} after charges");
        b.Append(baseline is null ? "; the baseline rule was not scored." : $"; the baseline rule {Rupees(baseline.NetPnl)}.").Append("\n\n");
        if (summary.Length > 0) b.Append(summary).Append("\n\n");
        if (proposed.Count == 0)
        {
            b.Append("No lesson proposed.\n");
        }
        else
        {
            b.Append("Lessons proposed (each is tested on past looks before the AI Trader reads it):\n");
            foreach (var (m, l) in proposed)
            {
                b.Append(CultureInfo.InvariantCulture, $"- M{m.Id}: {m.Text} (rests on decisions {string.Join(", ", l.DecisionIds)})\n");
            }
        }

        if (dropped.Count > 0)
        {
            b.Append("\nDropped:\n");
            foreach (var d in dropped) b.Append($"- \"{Cut(d.Text, 300)}\": {d.Why}\n");
        }

        return b.ToString();
    }

    private static readonly Regex DateLike = new(
        @"\b\d{4}-\d{2}-\d{2}\b|\b(?:19|20)\d{2}\b|\b\d{1,2}(?:st|nd|rd|th)?\s+(?:jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\b|\b(?:jan|feb|mar|apr|jun|jul|aug|sept?|oct|nov|dec)[a-z]*\.?\s+\d{1,2}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ContractLike = new(
        @"\b(?:NSE|BSE|NFO|BFO|MCX):|\b\d{4,6}\s*(?:CE|PE)\b|\b(?:CE|PE)\s*\d{4,6}\b|\b(?:NIFTY|BANKNIFTY|SENSEX)\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A rupee amount, or a figure an index level or a strike would be (grouped, or four digits or more that are not a time).</summary>
    private static readonly Regex PriceLike = new(
        @"₹\s*\d|\bRs\.?\s*\d|\bINR\s*\d|(?<![\d:.,])\d{1,3}(?:,\d{2,3})+(?![\d,])|(?<![\d:.,])\d{4,}(?![\d:])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Why a lesson's text cannot be kept, or null when it can: empty, too long, or naming a date, a contract or a price.</summary>
    public static string? Problem(string text) =>
        text.Length < 12 ? "too short to be a rule"
        : text.Length > MaxLessonChars ? $"longer than {MaxLessonChars} characters"
        : DateLike.IsMatch(text) ? "names a date"
        : ContractLike.IsMatch(text) ? "names a contract"
        : PriceLike.IsMatch(text) ? "names a price or a level"
        : null;

    /// <summary>
    /// The lessons in the Judge's answer: each checked (<see cref="Problem"/>), resting on at least one of
    /// <paramref name="dayDecisions"/>, at most <see cref="MaxLessons"/>. Null when the answer is not a JSON object with
    /// a lessons list. Public for tests.
    /// </summary>
    public static ReflectionRead? Read(string? answer, IReadOnlySet<long> dayDecisions)
    {
        if (AiJson.Object(answer) is not JsonObject obj || obj["lessons"] is not JsonArray list) return null;

        var kept = new List<ReflectedLesson>();
        var dropped = new List<DroppedLesson>();
        foreach (var item in list.OfType<JsonObject>())
        {
            string text = Flat(AiJson.Str(item, "lesson") ?? AiJson.Str(item, "rule") ?? AiJson.Str(item, "text") ?? string.Empty);
            if (text.Length == 0) continue;
            if (Problem(text) is string why)
            {
                dropped.Add(new DroppedLesson(text, why));
                continue;
            }

            var ids = Ids(item["decisions"] ?? item["decisionIds"]).Where(dayDecisions.Contains).Distinct().ToList();
            if (ids.Count == 0)
            {
                dropped.Add(new DroppedLesson(text, "rests on no decision of the day"));
                continue;
            }

            if (kept.Count >= MaxLessons)
            {
                dropped.Add(new DroppedLesson(text, $"more than {MaxLessons} lessons"));
                continue;
            }

            if (kept.Any(k => Fold(k.Text) == Fold(text))) continue;
            kept.Add(new ReflectedLesson(text, ids));
        }

        return new ReflectionRead(kept, dropped, Cut(Flat(AiJson.Str(obj, "summary") ?? string.Empty), 600));
    }

    /// <summary>Decision numbers as the Judge writes them: 4512, "4512" or "#4512".</summary>
    private static IEnumerable<long> Ids(JsonNode? node)
    {
        if (node is not JsonArray array) yield break;
        foreach (var v in array.OfType<JsonValue>())
        {
            if (v.TryGetValue(out long n)) yield return n;
            else if (v.TryGetValue(out double d) && d == Math.Floor(d)) yield return (long)d;
            else if (v.TryGetValue(out string? s) && long.TryParse(s.Trim().TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)) yield return parsed;
        }
    }

    private static long? ShadowOf(string resultJson) => LongIn(resultJson, "shadowPositionId");

    private static long? PositionIdOf(string planJson) => LongIn(planJson, "positionId");

    private static long? LongIn(string json, string name)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) is JsonObject o && o[name] is JsonValue v && v.TryGetValue(out long id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whitespace folded, quotes and a "Lesson:" lead taken off.</summary>
    private static string Flat(string text)
    {
        string flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim().Trim('"', '\'', '`').Trim();
        return Regex.Replace(flat, @"^lesson\s*[:\-]\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
    }

    /// <summary>A lesson's text for telling two apart: lower case, letters and digits only.</summary>
    private static string Fold(string text) => new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Whole rupees with Indian grouping and a sign: "+₹1,234", "−₹2,139", "₹0".</summary>
    public static string Rupees(decimal value)
    {
        decimal whole = Math.Round(value, 0, MidpointRounding.AwayFromZero);
        string digits = Math.Abs(whole).ToString("#,##0", CultureInfo.GetCultureInfo("en-IN"));
        return whole > 0 ? $"+₹{digits}" : whole < 0 ? $"−₹{digits}" : "₹0";
    }

    private static string Cut(string? text, int max)
    {
        var t = (text ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..(max - 1)] + "…";
    }
}
