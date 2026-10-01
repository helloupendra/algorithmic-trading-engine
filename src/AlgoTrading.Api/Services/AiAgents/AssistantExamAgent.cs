using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>
/// The Desk Assistant's weekly exam: a frozen bank of questions about finished
/// trading days, each asked several times, scored as pass@1 and pass^k, with
/// held-out days scored apart.
/// </summary>
/// <remarks>
/// <para>
/// The daily check (<see cref="AssistantCheckAgent"/>) asks about today, so its
/// answers move and its questions change: one day's score cannot be set
/// against another's. The exam asks about days that are over. The code reads
/// each answer from <c>get_runs</c> for that day when the question is written,
/// and the question never changes after that.
/// </para>
/// <para>
/// A trading day in four, chosen by a hash of the date, is held out. Nothing is
/// learnt from the exam, so the held-out days stay unseen by lessons, memories
/// and examples. Their score shows whether what the Assistant learns elsewhere
/// carries over.
/// </para>
/// <para>
/// A long exam must not hold up the other agents, which share the scheduler's
/// minute. So it asks a few questions a minute, keeps every ask as a row, and
/// carries on where it stopped after a restart. Before each ask, the
/// question's answer is read again. If the desk now reads differently (recap
/// runs leaving a day's totals, say), the question is set aside rather than
/// failed. A provider that gives no answer counts neither way.
/// </para>
/// </remarks>
public sealed class AssistantExamAgent(
    TradingDbContext db,
    AiGateway gateway,
    AiReportWriter reports,
    AiToolbox toolbox,
    IOptionsMonitor<AiSettings> settings,
    ILogger<AssistantExamAgent> logger,
    IMarketSessionService? sessions = null,
    TimeProvider? time = null) : IAiScheduledAgent
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One sitting's asks at a time in this API: a run on request and the scheduler's minute would ask the same question twice.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AgentKey => AiCatalog.AssistantExam;

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (MarketOpen(nowUtc) || !await Gate.WaitAsync(0, cancellationToken)) return false;
        try
        {
            var exam = await OpenExamAsync(cancellationToken);
            if (exam is null)
            {
                if (!Due(nowUtc) || await StartedOnAsync(IstTime.DateOf(nowUtc), cancellationToken)) return false;
                exam = await StartAsync("schedule", nowUtc, cancellationToken);
                if (exam is null) return false;
            }

            await AskSomeAsync(exam, cancellationToken);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>On request: carries on the open exam, or starts one. The scheduler asks the rest.</summary>
    public async Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var exam = await OpenExamAsync(cancellationToken) ?? await StartAsync("request", now, cancellationToken);
            return exam is null ? null : await AskSomeAsync(exam, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary><see cref="AiSettings.ExamDay"/> in IST, after <see cref="AiSettings.ExamAfterIst"/>.</summary>
    public bool Due(DateTime nowUtc)
    {
        var s = settings.CurrentValue;
        var ist = IstTime.ToIst(nowUtc);
        if (ist.DayOfWeek != s.ExamDay) return false;
        var after = TimeOnly.TryParseExact(s.ExamAfterIst, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? at : new TimeOnly(10, 30);
        return TimeOnly.FromDateTime(ist) >= after;
    }

    /// <summary>An exam never asks while NSE trades: the Assistant's model and the desk's database are busy then.</summary>
    private bool MarketOpen(DateTime nowUtc) => sessions?.IsMarketOpen(nowUtc, "NSE", "CM") == true;

    private Task<AiExam?> OpenExamAsync(CancellationToken cancellationToken) =>
        db.AiExams.Where(e => e.FinishedUtc == null).OrderBy(e => e.Id).FirstOrDefaultAsync(cancellationToken);

    private Task<bool> StartedOnAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var from = IstTime.FromIst(day.ToDateTime(TimeOnly.MinValue));
        var to = from.AddDays(1);
        return db.AiExams.AnyAsync(e => e.StartedUtc >= from && e.StartedUtc < to, cancellationToken);
    }

    private async Task<AiExam?> StartAsync(string trigger, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        await BuildBankAsync(nowUtc, cancellationToken);

        // A stable sample: the same questions come back week after week, and a
        // new day's questions join only where their hash falls in.
        var pool = await db.AiExamQuestions.AsNoTracking().Where(q => q.RetiredUtc == null)
            .Select(q => new { q.Id, q.Text }).ToListAsync(cancellationToken);
        if (pool.Count == 0)
        {
            logger.LogInformation("Assistant exam: the bank has no questions yet");
            return null;
        }

        var picked = pool.OrderBy(q => Stable(q.Text)).ThenBy(q => q.Id).Take(Math.Max(1, s.ExamMaxQuestions))
            .Select(q => q.Id).OrderBy(id => id).ToList();
        var exam = new AiExam
        {
            StartedUtc = nowUtc,
            Trigger = trigger,
            Repeats = Math.Clamp(s.ExamRepeats, 1, 5),
            QuestionIdsJson = JsonSerializer.Serialize(picked),
        };
        db.AiExams.Add(exam);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Assistant exam {Exam} started: {Count} questions, {Repeats} asks each", exam.Id, picked.Count, exam.Repeats);
        return exam;
    }

    /// <summary>
    /// The next few asks: every question once, then every question a second
    /// time, and so on, so an exam cut short still covers the bank. Answers the
    /// report when the last ask is done.
    /// </summary>
    private async Task<AiReport?> AskSomeAsync(AiExam exam, CancellationToken cancellationToken)
    {
        var ids = JsonSerializer.Deserialize<List<long>>(exam.QuestionIdsJson) ?? [];
        var done = (await db.AiExamAnswers.AsNoTracking().Where(a => a.ExamId == exam.Id)
                .Select(a => new { a.QuestionId, a.Attempt }).ToListAsync(cancellationToken))
            .Select(a => (a.QuestionId, a.Attempt)).ToHashSet();
        var plan = Enumerable.Range(1, exam.Repeats).SelectMany(k => ids.Select(id => (Id: id, Attempt: k)))
            .Where(p => !done.Contains(p)).ToList();
        var questions = await db.AiExamQuestions.AsNoTracking().Where(q => ids.Contains(q.Id)).ToDictionaryAsync(q => q.Id, cancellationToken);
        var days = new Dictionary<DateOnly, JsonObject?>();

        int asks = 0, handled = 0, budget = Math.Max(1, settings.CurrentValue.ExamAsksPerTick);
        foreach (var (id, attempt) in plan)
        {
            if (asks >= budget) break;
            handled++;
            var answer = new AiExamAnswer { ExamId = exam.Id, QuestionId = id, Attempt = attempt, CreatedUtc = _time.GetUtcNow().UtcDateTime };
            if (!questions.TryGetValue(id, out var q))
            {
                answer.Outcome = AiExamOutcome.Moved;
                answer.Truth = "the question is no longer in the bank";
            }
            else if (await TruthAsync(q, days, cancellationToken) is var truth && (truth is null || Differs(q, truth)))
            {
                answer.Outcome = AiExamOutcome.Moved;
                answer.Truth = truth?.Expected ?? "not readable now";
            }
            else
            {
                asks++;
                var result = await gateway.AskAsync(new AiAskInput(
                    AiCatalog.DeskAssistant, null, [new AiMessage("user", q.Text + " Answer briefly.")], null, 4096, 0.2,
                    $"exam-{exam.Id}-{q.Id}-{attempt}", "exam", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);
                answer.CallId = result.CallId;
                answer.Model = result.Model;
                answer.Seconds = Math.Round(result.Seconds, 1);
                answer.Answer = OneLine(result.Outcome == AiCallOutcome.Ok ? result.Text : result.Error, 600);
                answer.Outcome = result.Outcome != AiCallOutcome.Ok ? AiExamOutcome.NoAnswer
                    : Grade(q, result.Text) ? AiExamOutcome.Right : AiExamOutcome.Wrong;
            }

            db.AiExamAnswers.Add(answer);
            await db.SaveChangesAsync(cancellationToken);
        }

        return handled == plan.Count ? await FinishAsync(exam, cancellationToken) : null;
    }

    // ---------- the bank ----------

    /// <summary>
    /// Adds the questions of each finished trading day in the lookback that is
    /// not in the bank yet, and the fixed questions once. Never changes a
    /// question already written.
    /// </summary>
    public async Task<int> BuildBankAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        var today = IstTime.DateOf(nowUtc);
        var have = (await db.AiExamQuestions.AsNoTracking().Select(q => new { q.Day, q.Template }).Distinct().ToListAsync(cancellationToken));
        var daysIn = have.Where(h => h.Day is not null).Select(h => h.Day!.Value).ToHashSet();
        var added = new List<AiExamQuestion>();

        if (!have.Any(h => h.Day is null)) added.AddRange(FixedQuestions());

        for (var day = today.AddDays(-Math.Max(1, s.ExamLookbackDays)); day < today; day = day.AddDays(1))
        {
            if (daysIn.Contains(day) || day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var runs = await Read(AiToolNames.Runs, $$"""{"date":"{{day:yyyy-MM-dd}}"}""", cancellationToken);
            // A day with a run still going is not over yet; its turn comes at the next exam.
            if (runs?["runs"] is JsonArray list && list.OfType<JsonObject>().Any(r => r["active"]?.GetValue<bool>() == true)) continue;
            added.AddRange(DayQuestions(day, runs));
        }

        if (added.Count == 0) return 0;
        var now = _time.GetUtcNow().UtcDateTime;
        foreach (var q in added) q.CreatedUtc = now;
        db.AiExamQuestions.AddRange(added);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Assistant exam: {Count} questions added to the bank", added.Count);
        return added.Count;
    }

    /// <summary>
    /// One finished day's questions, from what <c>get_runs</c> answered for it:
    /// the day's totals, each account when there is more than one, and three
    /// runs (the worst, the best and the one in the middle). Public for tests.
    /// </summary>
    public static List<AiExamQuestion> DayQuestions(DateOnly day, JsonObject? runsAnswer)
    {
        var questions = new List<AiExamQuestion>();
        if (runsAnswer?["totals"] is not JsonObject totals || Num(totals["runs"]) is not double count || count < 1) return questions;
        var runs = (runsAnswer["runs"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(r => Num(r["netPnl"]) is not null && r["runId"] is not null).ToList();
        bool complete = runsAnswer["omitted"] is null && runs.Count == (int)count;
        string d = day.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        string set = HeldOut($"day:{day:yyyy-MM-dd}") ? AiExamSet.Holdout : AiExamSet.Practice;

        void Add(string template, string subject, string text, string kind, string expected, double? number, double tolerance) =>
            questions.Add(new AiExamQuestion
            {
                Template = template, Day = day, Subject = subject, Set = set, Text = text, Kind = kind,
                Expected = expected, Number = number, Tolerance = tolerance,
            });

        Add("day.runs", "", $"How many strategy runs did the desk have on {d}?", "number", Int(count), count, 0);
        if (Num(totals["netPnl"]) is double net)
        {
            Add("day.net", "", $"What was the net P&L of all the desk's runs on {d} together, after charges?", "number", Money(net), net, 1);
        }

        if (Num(totals["winners"]) is double winners)
        {
            Add("day.winners", "", $"How many runs made a profit after charges on {d}?", "number", Int(winners), winners, 0);
        }

        if (complete && runs.Count >= 2)
        {
            var byNet = runs.OrderBy(r => Num(r["netPnl"])).ToList();
            // A tie has no single answer.
            if (Num(byNet[0]["netPnl"]) != Num(byNet[1]["netPnl"]))
            {
                Add("day.worst", "", $"Which run had the lowest net P&L on {d}? Give its run id.", "id", Id(byNet[0]), double.Parse(Id(byNet[0]), CultureInfo.InvariantCulture), 0);
            }

            if (Num(byNet[^1]["netPnl"]) != Num(byNet[^2]["netPnl"]))
            {
                Add("day.best", "", $"Which run had the highest net P&L on {d}? Give its run id.", "id", Id(byNet[^1]), double.Parse(Id(byNet[^1]), CultureInfo.InvariantCulture), 0);
            }
        }

        if (totals["byAccount"] is JsonArray accounts && accounts.Count >= 2)
        {
            foreach (var a in accounts.OfType<JsonObject>().Take(3))
            {
                if (a["account"]?.GetValue<string>() is not { Length: > 0 } name || Num(a["netPnl"]) is not double accountNet) continue;
                Add("day.account", name, $"What was the {name} account's net P&L on {d}, after charges?", "number", Money(accountNet), accountNet, 1);
            }
        }

        var chosen = runs.Where(r => r["active"]?.GetValue<bool>() != true).OrderBy(r => Num(r["netPnl"])).ToList();
        var three = chosen.Count <= 3 ? chosen : [chosen[0], chosen[chosen.Count / 2], chosen[^1]];
        foreach (var r in three.DistinctBy(Id))
        {
            string id = Id(r);
            if (Num(r["netPnl"]) is double runNet) Add("run.net", id, $"What was run {id}'s net P&L, after charges?", "number", Money(runNet), runNet, 1);
            if (Num(r["charges"]) is double charges) Add("run.charges", id, $"How much did run {id} pay in charges?", "number", Money(charges), charges, 1);
            if (Num(r["closedTrades"]) is double trades) Add("run.trades", id, $"How many closed trades did run {id} make?", "number", Int(trades), trades, 0);
        }

        return questions;
    }

    /// <summary>Arithmetic the Assistant must get right with no tool: the desk's own conventions, lot sizes stated.</summary>
    public static List<AiExamQuestion> FixedQuestions()
    {
        AiExamQuestion Q(string key, string text, double answer) => new()
        {
            Template = $"fixed.{key}", Day = null, Subject = "", Set = HeldOut($"fixed:{key}") ? AiExamSet.Holdout : AiExamSet.Practice,
            Text = text, Kind = "number", Expected = Money(answer).Replace(".00", string.Empty), Number = answer, Tolerance = 0.5,
        };

        return
        [
            Q("buy-pnl", "If I buy 2 lots of a NIFTY option at ₹80 with a lot size of 65 and sell them at ₹95, what is my P&L before charges?", 1950),
            Q("sell-pnl", "If I sell 1 lot of a BANKNIFTY option at ₹300 with a lot size of 30 and buy it back at ₹340, what is my P&L before charges?", -1200),
            Q("expiry-loss", "I bought 3 lots of a SENSEX option at ₹150 with a lot size of 20 and it expired at ₹0.05. What is my P&L before charges?", -8997),
            Q("net", "A run made ₹12,500 before charges and paid ₹3,180 in charges. What is its net P&L?", 9320),
            Q("sum", "Three runs made +₹4,200, −₹7,850 and +₹1,130 net. What is their total net P&L?", -2520),
            Q("straddle", "A short straddle sold 1 NIFTY lot of 65 with ₹210 for the call and ₹190 for the put, and both expired worthless. What is the P&L before charges?", 26000),
        ];
    }

    /// <summary>The question as the desk answers it now: the fixed ones as written, a day's from its runs again; null when unreadable.</summary>
    private async Task<AiExamQuestion?> TruthAsync(AiExamQuestion q, Dictionary<DateOnly, JsonObject?> days, CancellationToken cancellationToken)
    {
        if (q.Day is not DateOnly day) return q;
        if (!days.TryGetValue(day, out var runs))
        {
            runs = await Read(AiToolNames.Runs, $$"""{"date":"{{day:yyyy-MM-dd}}"}""", cancellationToken);
            days[day] = runs;
        }

        return DayQuestions(day, runs).FirstOrDefault(t => t.Template == q.Template && t.Subject == q.Subject);
    }

    private static bool Differs(AiExamQuestion frozen, AiExamQuestion now) => frozen.Kind == "number"
        ? frozen.Number is not double a || now.Number is not double b || Math.Abs(a - b) > frozen.Tolerance
        : !string.Equals(frozen.Expected, now.Expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The daily check's grading, after the day the question names is taken out
    /// of the answer: "29 Sep 2026" holds two numbers a count could match.
    /// Public for tests.
    /// </summary>
    public static bool Grade(AiExamQuestion q, string answer)
    {
        string text = q.Day is DateOnly day ? WithoutDay(answer, day) : answer;
        return AssistantCheckAgent.Grade(new AssistantCheckAgent.Question(q.Text, q.Kind, q.Expected, q.Number, q.Tolerance), text);
    }

    private static string WithoutDay(string text, DateOnly day)
    {
        string mon = day.ToString("MMM", CultureInfo.InvariantCulture), month = day.ToString("MMMM", CultureInfo.InvariantCulture);
        string months = $"(?:{Regex.Escape(month)}|{Regex.Escape(mon)})\\.?";
        string dd = $"0?{day.Day}(?:st|nd|rd|th)?";
        string t = Regex.Replace(text, $@"\b{dd}\s+{months}(?:,?\s+{day.Year})?\b", " ", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, $@"\b{months}\s+{dd}(?:,?\s+{day.Year})?\b", " ", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\b\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}\b", " ");
        return Regex.Replace(t, $@"\b{day.Year}\b", " ");
    }

    // ---------- the score ----------

    /// <summary>One question's asks in an exam, for the score.</summary>
    public sealed record Sitting(AiExamQuestion Question, IReadOnlyList<AiExamAnswer> Asks);

    /// <summary>A score over some questions.</summary>
    /// <param name="Scored">Questions asked every time with an answer each time, and never set aside: pass^k is over these.</param>
    public sealed record Score(int Questions, int Scored, int PassAll, int RightAsks, int AnsweredAsks, int SetAside, int NoAnswer)
    {
        public double? PassK => Scored == 0 ? null : (double)PassAll / Scored;

        public double? Pass1 => AnsweredAsks == 0 ? null : (double)RightAsks / AnsweredAsks;
    }

    /// <summary>pass^k, pass@1 and what could not be counted, over these sittings. Public for tests.</summary>
    public static Score ScoreOf(IReadOnlyCollection<Sitting> sittings, int repeats)
    {
        int scored = 0, passAll = 0, right = 0, answered = 0, setAside = 0, noAnswer = 0;
        foreach (var s in sittings)
        {
            right += s.Asks.Count(a => a.Outcome == AiExamOutcome.Right);
            answered += s.Asks.Count(a => a.Outcome is AiExamOutcome.Right or AiExamOutcome.Wrong);
            if (s.Asks.Any(a => a.Outcome == AiExamOutcome.Moved)) setAside++;
            else if (s.Asks.Any(a => a.Outcome == AiExamOutcome.NoAnswer)) noAnswer++;
            else if (s.Asks.Count >= repeats)
            {
                scored++;
                if (s.Asks.All(a => a.Outcome == AiExamOutcome.Right)) passAll++;
            }
        }

        return new Score(sittings.Count, scored, passAll, right, answered, setAside, noAnswer);
    }

    private async Task<AiReport?> FinishAsync(AiExam exam, CancellationToken cancellationToken)
    {
        var ids = JsonSerializer.Deserialize<List<long>>(exam.QuestionIdsJson) ?? [];
        var questions = await db.AiExamQuestions.AsNoTracking().Where(q => ids.Contains(q.Id)).ToDictionaryAsync(q => q.Id, cancellationToken);
        var answers = await db.AiExamAnswers.AsNoTracking().Where(a => a.ExamId == exam.Id).OrderBy(a => a.Attempt).ToListAsync(cancellationToken);
        var sittings = answers.GroupBy(a => a.QuestionId).Where(g => questions.ContainsKey(g.Key))
            .Select(g => new Sitting(questions[g.Key], g.ToList())).ToList();

        var all = ScoreOf(sittings, exam.Repeats);
        var practice = ScoreOf(sittings.Where(s => s.Question.Set == AiExamSet.Practice).ToList(), exam.Repeats);
        var holdout = ScoreOf(sittings.Where(s => s.Question.Set == AiExamSet.Holdout).ToList(), exam.Repeats);
        var byTemplate = sittings.GroupBy(s => s.Question.Template).OrderBy(g => g.Key)
            .Select(g => (Template: g.Key, Score: ScoreOf(g.ToList(), exam.Repeats))).ToList();
        var wrong = sittings.Where(s => s.Asks.Any(a => a.Outcome == AiExamOutcome.Wrong))
            .OrderBy(s => s.Question.Set).ThenBy(s => s.Question.Template).ThenBy(s => s.Question.Id).ToList();

        string k = exam.Repeats.ToString(CultureInfo.InvariantCulture);
        var body = new StringBuilder();
        body.Append($"**pass^{k} {Pct(practice.PassK)} on practice, {Pct(holdout.PassK)} on held-out days.** ");
        body.Append($"{all.Questions} questions, each asked {k} times; pass@1 {Pct(all.Pass1)} of {all.AnsweredAsks} answered asks.");
        if (all.SetAside > 0) body.Append($" {all.SetAside} set aside: the desk now reads their day differently.");
        if (all.NoAnswer > 0) body.Append($" {all.NoAnswer} had an ask with no answer from the provider.");
        body.Append("\n\n| | Questions | Scored | pass^").Append(k).Append(" | pass@1 |\n|---|---|---|---|---|\n");
        foreach (var (name, score) in new[] { ("Practice", practice), ("Held-out", holdout), ("All", all) })
        {
            body.Append($"| {name} | {score.Questions} | {score.Scored} | {Pct(score.PassK)} | {Pct(score.Pass1)} |\n");
        }

        body.Append("\n| Question type | Questions | pass^").Append(k).Append(" | pass@1 |\n|---|---|---|---|\n");
        foreach (var (template, score) in byTemplate) body.Append($"| {template} | {score.Questions} | {Pct(score.PassK)} | {Pct(score.Pass1)} |\n");

        if (wrong.Count > 0)
        {
            body.Append("\n| Wrong at least once | Set | Expected | Answers |\n|---|---|---|---|\n");
            foreach (var s in wrong.Take(40))
            {
                string asks = string.Join(" / ", s.Asks.Select(a => a.Outcome == AiExamOutcome.Right ? "✓" : a.Outcome == AiExamOutcome.Wrong ? "✗ " + OneLine(a.Answer, 80) : a.Outcome));
                body.Append($"| {Cell(s.Question.Text)} | {s.Question.Set} | {Cell(s.Question.Expected)} | {Cell(asks)} |\n");
            }

            if (wrong.Count > 40) body.Append($"\n{wrong.Count - 40} more in the exam's answers.\n");
        }

        JsonObject Part(Score score) => new()
        {
            ["questions"] = score.Questions, ["scored"] = score.Scored, ["passAll"] = score.PassAll, ["passK"] = Round(score.PassK),
            ["rightAsks"] = score.RightAsks, ["answeredAsks"] = score.AnsweredAsks, ["pass1"] = Round(score.Pass1),
            ["setAside"] = score.SetAside, ["noAnswer"] = score.NoAnswer,
        };

        var data = new JsonObject
        {
            ["examId"] = exam.Id,
            ["repeats"] = exam.Repeats,
            ["all"] = Part(all),
            ["practice"] = Part(practice),
            ["holdout"] = Part(holdout),
            ["templates"] = new JsonArray(byTemplate.Select(t => (JsonNode)new JsonObject { ["template"] = t.Template, ["score"] = Part(t.Score) }).ToArray()),
            ["wrong"] = new JsonArray(wrong.Take(100).Select(s => (JsonNode)new JsonObject
            {
                ["questionId"] = s.Question.Id, ["question"] = s.Question.Text, ["set"] = s.Question.Set, ["expected"] = s.Question.Expected,
                ["answers"] = new JsonArray(s.Asks.Select(a => (JsonNode)new JsonObject
                {
                    ["outcome"] = a.Outcome, ["answer"] = OneLine(a.Answer, 300), ["callId"] = a.CallId,
                }).ToArray()),
            }).ToArray()),
        };

        bool counted = all.Scored > 0;
        var day = IstTime.DateOf(exam.StartedUtc);
        var report = await reports.SaveAsync(AgentKey, AiReportSubject.Exam, exam.Id.ToString(CultureInfo.InvariantCulture), day,
            counted ? AiReportStatus.Ok : AiReportStatus.Failed, null,
            $"Assistant exam: pass^{k} {Pct(practice.PassK)} practice, {Pct(holdout.PassK)} held out ({all.Questions} questions)",
            body.ToString(), data.ToJsonString(Json),
            counted ? string.Empty : "No question was answered every time: the provider, or the bank, needs a look.", cancellationToken);

        var row = await db.AiExams.FirstAsync(e => e.Id == exam.Id, cancellationToken);
        row.FinishedUtc = _time.GetUtcNow().UtcDateTime;
        row.ReportId = report?.Id;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Assistant exam {Exam} finished: pass^{K} {Practice} practice, {Holdout} held out", exam.Id, k, Pct(practice.PassK), Pct(holdout.PassK));
        return report;
    }

    // ---------- helpers ----------

    /// <summary>One day in four held out, by a hash of its key: stable across restarts and machines.</summary>
    public static bool HeldOut(string key) => Stable("holdout:" + key) % 4 == 0;

    private static uint Stable(string text) => BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0);

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
            logger.LogInformation("Assistant exam: {Tool} could not be read ({Error})", tool, ex.GetType().Name);
            return null;
        }
    }

    /// <summary>A number from a tool's JSON, whether it was parsed (a double) or built in code (an int, a decimal).</summary>
    private static double? Num(JsonNode? node) => node is not JsonValue v ? null
        : v.TryGetValue(out double d) ? d
        : v.TryGetValue(out long l) ? l
        : v.TryGetValue(out int i) ? i
        : v.TryGetValue(out decimal m) ? (double)m
        : null;

    private static string Id(JsonObject run) => run["runId"]!.ToString();

    private static string Int(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static string Money(double value) => value.ToString("#,##0.00", CultureInfo.InvariantCulture);

    private static double? Round(double? share) => share is double s ? Math.Round(s, 3) : null;

    private static string Pct(double? share) => share is double s ? s.ToString("P0", CultureInfo.InvariantCulture) : "—";

    private static string OneLine(string text, int max)
    {
        string flat = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");
}
