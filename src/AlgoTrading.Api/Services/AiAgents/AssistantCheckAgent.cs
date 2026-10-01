using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Services.AgentMemory;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>
/// The Desk Assistant's daily check: questions whose answers the code knows,
/// asked the way the owner asks, and graded without a model.
/// </summary>
/// <remarks>
/// <para>
/// After the close on each trading day the check reads the desk through the
/// same tools the Assistant uses. That gives it the day's runs, the worst one
/// and its money, the open legs, the live incidents, the latest checkup, the
/// NIFTY chain and the forecasts. It turns each into a question with a known
/// answer, and asks the Assistant in its real configuration: its prompt, its
/// chain, its tools. A question whose answer the desk does not have today (no
/// runs on a holiday, no chain) is left out.
/// </para>
/// <para>
/// Grading is plain code. Numbers are read out of the answer (₹1,10,132.75,
/// −110,132.75, 1.1 lakh, 59.8k) after its citations, times and dates are
/// removed, and a question passes when one of them is the known answer within
/// its tolerance. The answer is read again from the tools after the model
/// answers, because a run still trading (MCX runs past the NSE close) moves
/// the day's money meanwhile; a figure anywhere between the two readings
/// passes. A model or prompt change that makes the Assistant misread
/// the desk shows up as a lower score the same evening. The result is a report
/// (subject <c>check</c>, one per day) on the Reports tab, each question
/// linked to its call.
/// </para>
/// </remarks>
public sealed class AssistantCheckAgent(
    TradingDbContext db,
    AiGateway gateway,
    AiReportWriter reports,
    AiToolbox toolbox,
    AiSchedulerState schedule,
    IOptionsMonitor<AiSettings> settings,
    ILogger<AssistantCheckAgent> logger,
    IMarketSessionService? sessions = null,
    AiMemoryService? memory = null,
    IEnumerable<IAiLessonNotifier>? notifiers = null,
    TimeProvider? time = null) : IAiScheduledAgent
{
    /// <summary>The share of questions that must pass for the day's check to count as ok.</summary>
    public const double PassMark = 0.8;

    /// <summary>The Judge's brief for turning a wrong answer into one lesson for the owner to approve.</summary>
    public const string LessonPrompt =
        "You help improve the desk assistant of an Indian F&O paper-trading desk. It answered a question wrong. You get " +
        "the question, the right answer (read by code from the desk's own records), the assistant's answer, and the tools " +
        "it called. Write ONE lesson, under 250 characters, that would make it answer this kind of question right next " +
        "time: which tool or field to read, or a convention of the desk (net of charges, Indian digit grouping, IST). " +
        "Make it general, with no numbers from today. If the mistake is a slip no rule would prevent, or the right answer " +
        "itself looks doubtful, reply NONE. Reply with the lesson or NONE, nothing else.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AgentKey => AiCatalog.AssistantCheck;

    /// <summary>One question and how it is graded.</summary>
    /// <param name="Kind"><c>number</c> (within <paramref name="Tolerance"/>, sign ignored), <c>id</c> (a standalone number) or <c>word</c>.</param>
    /// <param name="Also">The answer the tools gave after the model answered, when it moved: a number then passes anywhere between the two, an id as either.</param>
    public sealed record Question(string Text, string Kind, string Expected, double? Number, double Tolerance, double? Also = null);

    public sealed record Graded(Question Question, bool Pass, string Answer, long? CallId, string Model, double Seconds, string? Error)
    {
        /// <summary>The memories the answer was given.</summary>
        public IReadOnlyList<long> MemoryIds { get; init; } = [];
    }

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (!Due(nowUtc)) return false;
        string day = IstTime.DateOf(nowUtc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var due = await reports.DueAsync(AgentKey, AiReportSubject.Check, [day], cancellationToken);
        if (due.Count == 0) return false;

        schedule.Worked(AgentKey, nowUtc);
        await CheckAsync(cancellationToken);
        return true;
    }

    public Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken) => CheckAsync(cancellationToken)!;

    /// <summary>
    /// An NSE trading day, after <see cref="AiSettings.AssistantCheckAfterIst"/> IST. An exchange
    /// holiday on a weekday (Gandhi Jayanti, 2 Oct) has no runs or forecasts to ask about.
    /// Without the calendar every weekday counts, as the market-open job assumes.
    /// </summary>
    public bool Due(DateTime nowUtc)
    {
        var ist = IstTime.ToIst(nowUtc);
        if (ist.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        if (sessions is not null && !sessions.GetSessionInfo(IstTime.MiddayUtc(IstTime.DateOf(nowUtc)), "NSE", "CM").IsTradingDay) return false;
        var after = TimeOnly.TryParseExact(settings.CurrentValue.AssistantCheckAfterIst, "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var at) ? at : new TimeOnly(16, 40);
        return TimeOnly.FromDateTime(ist) >= after;
    }

    private async Task<AiReport?> CheckAsync(CancellationToken cancellationToken)
    {
        var day = IstTime.DateOf(_time.GetUtcNow().UtcDateTime);
        var questions = await QuestionsAsync(cancellationToken);
        if (questions.Count == 0)
        {
            logger.LogInformation("Assistant check {Day}: the desk has nothing to ask about today", day);
            return null;
        }

        var graded = new List<Graded>();
        int n = 0;
        foreach (var q in questions)
        {
            n++;
            var result = await gateway.AskAsync(new AiAskInput(
                AiCatalog.DeskAssistant, null, [new AiMessage("user", q.Text + " Answer briefly.")], null, 4096, 0.2,
                $"check-{day:yyyyMMdd}-{n}", "check", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);

            // A run still trading (MCX runs past the NSE close) moves the day's
            // money while the model reads it, so the truth is read again after
            // the answer and anything between the two readings passes.
            var after = (await QuestionsAsync(cancellationToken)).FirstOrDefault(a => a.Text == q.Text);
            var asked = Moved(q, after);
            bool answered = result.Outcome == AiCallOutcome.Ok;
            bool pass = answered && Grade(asked, result.Text);
            graded.Add(new Graded(asked, pass, result.Text, result.CallId, result.Model, result.Seconds, answered ? null : result.Error)
            {
                MemoryIds = result.MemoryIds,
            });

            // Outcomes: the grade counts on every memory the answer was given.
            if (answered && memory is not null) await memory.CountCheckAsync(result.CallId, pass, cancellationToken);
        }

        var lessons = await ProposeLessonsAsync(graded, day, cancellationToken);

        int passed = graded.Count(g => g.Pass);
        double share = (double)passed / graded.Count;
        string status = share >= PassMark ? AiReportStatus.Ok : AiReportStatus.Invalid;

        var body = new StringBuilder();
        body.Append($"**{passed} of {graded.Count} right** ({share:P0}); the day passes at {PassMark:P0}.\n\n");
        body.Append("| # | Question | Expected | Answer | | Model | s |\n|---|---|---|---|---|---|---|\n");
        for (int i = 0; i < graded.Count; i++)
        {
            var g = graded[i];
            string answer = g.Error is not null ? $"no answer: {g.Error}" : OneLine(g.Answer, 140);
            body.Append($"| {i + 1} | {Cell(g.Question.Text)} | {Cell(g.Question.Expected)} | {Cell(answer)} | {(g.Pass ? "✓" : "✗")} | {Cell(g.Model)} | {g.Seconds:0} |\n");
        }

        int withMemory = graded.Count(g => g.MemoryIds.Count > 0);
        if (withMemory > 0 || lessons.Count > 0)
        {
            body.Append($"\nMemory: {withMemory} of {graded.Count} answers read memories");
            body.Append(lessons.Count > 0
                ? "; lessons tested today: " + string.Join("; ", lessons.Select(l =>
                    $"M{l.Id} {(l.Status == AiMemoryStatus.Active ? "used" : "dropped")} ({l.DecidedBy.Replace("check: ", string.Empty)})")) + ".\n"
                : ".\n");
        }

        var data = new JsonObject
        {
            ["passed"] = passed,
            ["total"] = graded.Count,
            ["score"] = Math.Round(share, 3),
            ["questions"] = new JsonArray(graded.Select(g => (JsonNode)new JsonObject
            {
                ["question"] = g.Question.Text,
                ["kind"] = g.Question.Kind,
                ["expected"] = g.Question.Expected,
                ["pass"] = g.Pass,
                ["answer"] = OneLine(g.Answer, 600),
                ["callId"] = g.CallId,
                ["model"] = g.Model,
                ["seconds"] = g.Seconds,
                ["error"] = g.Error,
                ["memoryIds"] = new JsonArray(g.MemoryIds.Select(id => (JsonNode)id).ToArray()),
            }).ToArray()),
            ["lessons"] = new JsonArray(lessons.Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id, ["used"] = l.Status == AiMemoryStatus.Active, ["verdict"] = l.DecidedBy,
            }).ToArray()),
        };

        var report = await reports.SaveAsync(AgentKey, AiReportSubject.Check, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), day, status, null,
            $"Assistant check: {passed} of {graded.Count} right", body.ToString(), data.ToJsonString(Json),
            status == AiReportStatus.Ok ? string.Empty : $"Below the pass mark: {passed} of {graded.Count}.", cancellationToken);

        if (lessons.Count > 0)
        {
            foreach (var lesson in lessons) lesson.SourceReportId = report?.Id;
            await db.SaveChangesAsync(cancellationToken);
            foreach (var notifier in notifiers ?? [])
            {
                try
                {
                    await notifier.LessonsProposedAsync(lessons, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Assistant check: telling the owner about {Count} new lessons failed", lessons.Count);
                }
            }
        }

        return report;
    }

    /// <summary>
    /// For each question it got wrong (at most <see cref="AiSettings.MaxLessonsPerCheck"/>), the Judge
    /// writes one lesson, and the lesson is tested before it is used: the Assistant is asked that
    /// question again with the lesson in its memory, and two questions it got right. It is used only if
    /// it fixes the one and keeps the others; otherwise it is dropped. The owner does not have to
    /// approve each one (1 Oct: "I can't keep saying yes or no"): the evidence decides, and the owner
    /// can still take any lesson out. A kind of question that already has a lesson, or had one dropped
    /// lately, is not asked about again.
    /// </summary>
    private async Task<List<AiMemory>> ProposeLessonsAsync(List<Graded> graded, DateOnly day, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        var proposed = new List<AiMemory>();
        if (memory is null || !s.LessonsFromCheck || !s.HasMemory(AiCatalog.DeskAssistant)) return proposed;

        int asked = 0;
        foreach (var g in graded.Where(g => !g.Pass && g.Error is null && g.CallId is not null))
        {
            if (asked >= s.MaxLessonsPerCheck) break;
            if (await memory.LessonBlockedAsync(AiCatalog.DeskAssistant, g.Question.Text, cancellationToken)) continue;
            asked++;

            string? lesson = await LessonAsync(g, day, asked, cancellationToken);
            if (lesson is null) continue;
            var saved = await memory.ProposeAsync(AiCatalog.DeskAssistant, lesson, g.Question.Text, g.CallId, null, cancellationToken);
            if (saved is null) continue;

            string verdict = await VerifyAsync(saved, g, graded, day, asked, cancellationToken);
            await memory.UpdateAsync(saved.Id, null, verdict == Verified ? AiMemoryStatus.Active : AiMemoryStatus.Rejected,
                verdict, cancellationToken);
            proposed.Add(saved);
        }

        return proposed;
    }

    /// <summary>The decider written on a lesson that fixed its question and broke nothing.</summary>
    public const string Verified = "check: fixed its question";

    /// <summary>The decider written on a lesson that did not fix the question it came from.</summary>
    public const string DidNotFix = "check: did not fix its question";

    /// <summary>The decider written on a lesson that fixed its question but turned a right answer wrong.</summary>
    public const string BrokeAnother = "check: broke a right answer";

    /// <summary>The questions a lesson must keep right besides the one it is for.</summary>
    public const int KeepChecks = 2;

    private async Task<string> VerifyAsync(AiMemory lesson, Graded failed, List<Graded> graded, DateOnly day, int n,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<long> trial = [lesson.Id];
        if (!await AskAgainAsync(failed.Question, trial, $"check-{day:yyyyMMdd}-verify-{n}-0", cancellationToken)) return DidNotFix;

        int k = 0;
        foreach (var right in graded.Where(x => x.Pass).Take(KeepChecks))
        {
            k++;
            if (!await AskAgainAsync(right.Question, trial, $"check-{day:yyyyMMdd}-verify-{n}-{k}", cancellationToken)) return BrokeAnother;
        }

        return Verified;
    }

    /// <summary>One question asked again with a lesson on trial, graded against the tools' answer as it stands now.</summary>
    private async Task<bool> AskAgainAsync(Question q, IReadOnlyList<long> trial, string conversation, CancellationToken cancellationToken)
    {
        var result = await gateway.AskAsync(new AiAskInput(
            AiCatalog.DeskAssistant, null, [new AiMessage("user", q.Text + " Answer briefly.")], null, 4096, 0.2,
            conversation, "check", AgentKey, null, TrialMemoryIds: trial), NullAiStreamSink.Instance, cancellationToken);
        if (result.Outcome != AiCallOutcome.Ok) return false;
        var now = (await QuestionsAsync(cancellationToken)).FirstOrDefault(a => a.Text == q.Text);
        return Grade(Moved(q with { Also = null }, now), result.Text);
    }

    private async Task<string?> LessonAsync(Graded g, DateOnly day, int n, CancellationToken cancellationToken)
    {
        string tools = "none";
        string? toolsJson = await db.AiCalls.AsNoTracking().Where(c => c.Id == g.CallId).Select(c => c.ToolsJson).FirstOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(toolsJson) && JsonNode.Parse(toolsJson) is JsonArray steps && steps.Count > 0)
        {
            tools = string.Join("; ", steps.OfType<JsonObject>().Select(t =>
                $"{t["name"]?.GetValue<string>()} {t["arguments"]?.GetValue<string>()} ({(t["ok"]?.GetValue<bool>() == true ? "ok" : "failed")})"));
        }

        string question =
            $"Question: {g.Question.Text}\n" +
            $"Right answer: {g.Question.Expected}\n" +
            $"Assistant's answer: {OneLine(g.Answer, 1500)}\n" +
            $"Tools it called: {tools}";

        var judge = (await new AiSettingsStore(db).LoadAsync(cancellationToken)).Tier("judge").Chain;
        var result = await gateway.AskAsync(new AiAskInput(
            AgentKey, null, [new AiMessage("user", question)], LessonPrompt, 600, 0.2,
            $"check-{day:yyyyMMdd}-lesson-{n}", "check", AgentKey, null, judge), NullAiStreamSink.Instance, cancellationToken);
        return result.Outcome == AiCallOutcome.Ok ? LessonFrom(result.Text) : null;
    }

    /// <summary>The lesson in a Judge's reply, or null for NONE or nothing. Public for tests.</summary>
    public static string? LessonFrom(string reply)
    {
        string text = string.Join(' ', (reply ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim().Trim('"', '\'', '`').Trim();
        text = Regex.Replace(text, @"^lesson\s*[:\-]\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        if (text.Length < 12 || text.StartsWith("NONE", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Length <= 300) return text;
        int cut = text.LastIndexOf(' ', 299);
        return text[..(cut > 200 ? cut : 299)] + "…";
    }

    /// <summary>Today's questions, each with the answer the desk's own tools give.</summary>
    public async Task<List<Question>> QuestionsAsync(CancellationToken cancellationToken)
    {
        var questions = new List<Question>();

        var runs = await Read(AiToolNames.Runs, "{}", cancellationToken);
        if (runs?["runs"] is JsonArray list && list.Count > 0 && runs["totals"] is JsonObject totals)
        {
            int count = totals["runs"]!.GetValue<int>();
            questions.Add(new Question("How many strategy runs does the desk have today, counting the ones still running?", "number",
                count.ToString(CultureInfo.InvariantCulture), count, 0));
            questions.Add(new Question("What is the net P&L of all of today's runs together, after charges?", "number",
                Money(totals["netPnl"]), Num(totals["netPnl"]), 1));

            var worst = list.OfType<JsonObject>().OrderBy(r => Num(r["netPnl"]) ?? 0).First();
            long id = worst["runId"]!.GetValue<long>();
            questions.Add(new Question("Which run has the lowest net P&L today? Give its run id.", "id", id.ToString(CultureInfo.InvariantCulture), id, 0));
            questions.Add(new Question($"What is run {id}'s net P&L today, after charges?", "number", Money(worst["netPnl"]), Num(worst["netPnl"]), 1));
            questions.Add(new Question($"How much did run {id} pay in charges today?", "number", Money(worst["charges"]), Num(worst["charges"]), 1));
        }

        var open = await Read(AiToolNames.OpenPositions, "{}", cancellationToken);
        if (open?["totals"]?["legs"] is JsonNode legs)
        {
            int n = legs.GetValue<int>();
            questions.Add(new Question("How many open legs are there right now across all runs and manual books?", "number",
                n.ToString(CultureInfo.InvariantCulture), n, 0));
        }

        var incidents = await Read(AiToolNames.Incidents, """{"which":"live"}""", cancellationToken);
        if (incidents?["incidents"] is JsonArray live)
        {
            questions.Add(new Question("How many live Sentinel incidents are there right now (open or acknowledged)?", "number",
                live.Count.ToString(CultureInfo.InvariantCulture), live.Count, 0));
        }

        var checkup = await Read(AiToolNames.Checkup, "{}", cancellationToken);
        if (checkup?["verdict"] is JsonValue verdict && verdict.TryGetValue(out string? v) && !string.IsNullOrWhiteSpace(v))
        {
            questions.Add(new Question("What was the verdict of the latest desk checkup? One word.", "word", v, null, 0));
        }

        var chain = await Read(AiToolNames.OptionChain, """{"underlying":"NIFTY"}""", cancellationToken);
        if (Num(chain?["putCallRatio"]) is double pcr)
        {
            questions.Add(new Question("What is NIFTY's put-call ratio on the nearest expiry right now?", "number",
                pcr.ToString("0.00", CultureInfo.InvariantCulture), pcr, 0.011));
        }

        if (Num(chain?["maxPain"]) is double maxPain)
        {
            questions.Add(new Question("What is NIFTY's max pain strike on the nearest expiry?", "number",
                maxPain.ToString("0", CultureInfo.InvariantCulture), maxPain, 0));
        }

        var forecasts = await Read(AiToolNames.Forecasts, "{}", cancellationToken);
        if (forecasts?["forecasts"] is JsonArray issued && issued.Count > 0)
        {
            questions.Add(new Question("How many forecasts did the desk issue for today's session?", "number",
                issued.Count.ToString(CultureInfo.InvariantCulture), issued.Count, 0));
        }

        if (questions.Count > 0)
        {
            questions.Add(new Question(
                "If I sell 1 lot of a NIFTY option at ₹120 with a lot size of 65 and it expires at ₹35, what is my P&L before charges?",
                "number", "5,525", 5525, 0.5));
        }

        return questions;
    }

    /// <summary>Whether an answer carries the expected result. Public for tests.</summary>
    public static bool Grade(Question q, string answer)
    {
        string text = Plain(answer);
        return q.Kind switch
        {
            "word" => text.Contains(q.Expected, StringComparison.OrdinalIgnoreCase),
            "id" => new[] { q.Number, q.Also }.OfType<double>()
                .Any(id => Regex.IsMatch(text, $@"(?<!\d|\d\.){id.ToString("0", CultureInfo.InvariantCulture)}(?!\d|\.\d)")),
            _ => q.Number is double expected && Numbers(text).Any(x => Between(Math.Abs(x), Math.Abs(expected), Math.Abs(q.Also ?? expected), q.Tolerance)),
        };
    }

    private static bool Between(double x, double a, double b, double tolerance) =>
        x >= Math.Min(a, b) - tolerance && x <= Math.Max(a, b) + tolerance;

    /// <summary><paramref name="before"/>, carrying the second reading when the tools' answer moved while the model answered.</summary>
    private static Question Moved(Question before, Question? after) =>
        before.Kind != "word" && after?.Number is double now && before.Number is double then && Math.Abs(now - then) > before.Tolerance
            ? before with { Also = now, Expected = $"{before.Expected} → {after.Expected}" }
            : before;

    /// <summary>
    /// The answer without what could look like a figure and is not one: the
    /// tool citations "(get_runs, 20:10 IST)", times and dates. A count like
    /// 10 would otherwise match the "10" of a citation's time.
    /// </summary>
    public static string Plain(string answer)
    {
        string t = Regex.Replace(answer, @"\(\s*get_[a-z_]+[^)]*\)", " ");
        t = Regex.Replace(t, @"\b\d{4}-\d{2}-\d{2}\b", " ");
        t = Regex.Replace(t, @"\b\d{1,2}:\d{2}(?::\d{2})?\b", " ");
        return t;
    }

    /// <summary>Every number in a text, as the desk writes money: Indian grouping, ₹, a unicode minus, lakh, crore and k.</summary>
    public static List<double> Numbers(string text)
    {
        var found = new List<double>();
        string t = text.Replace('\u2212', '-').Replace("₹", string.Empty);
        foreach (Match m in Regex.Matches(t, @"(?<![\w.])-?\d[\d,]*(?:\.\d+)?(?:\s*(?<unit>lakh|lakhs|lac|l|crore|cr|k)\b)?", RegexOptions.IgnoreCase))
        {
            string raw = m.Value;
            string unit = m.Groups["unit"].Value.ToLowerInvariant();
            if (unit.Length > 0) raw = raw[..^m.Groups["unit"].Length].Trim();
            if (!double.TryParse(raw.Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) continue;
            value *= unit switch
            {
                "lakh" or "lakhs" or "lac" or "l" => 100_000,
                "crore" or "cr" => 10_000_000,
                "k" => 1_000,
                _ => 1,
            };
            found.Add(value);
        }

        return found;
    }

    private async Task<JsonObject?> Read(string tool, string args, CancellationToken cancellationToken)
    {
        var t = toolbox.Find(tool);
        if (t is null) return null;
        try
        {
            var output = await t.RunAsync(AiToolArgs.Parse(args), cancellationToken);
            return JsonNode.Parse(JsonSerializer.Serialize(output.Data, Json)) as JsonObject;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("Assistant check: {Tool} could not be read ({Error}); its questions are left out", tool, ex.GetType().Name);
            return null;
        }
    }

    private static double? Num(JsonNode? node) => node is JsonValue v && v.TryGetValue(out double d) ? d : null;

    private static string Money(JsonNode? node) => Num(node) is double d ? d.ToString("#,##0.00", CultureInfo.InvariantCulture) : "?";

    private static string OneLine(string text, int max)
    {
        string flat = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");
}
