using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Api.Services.AgentMemory;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>One side of a past look in a lesson's test: what the answer was, how the rules judged it, and what it made.</summary>
/// <param name="Action">The plan's action; empty when the answer was not a plan or no model answered.</param>
/// <param name="Rule">ok, the rule that refused it, <c>unreadable</c> or <c>no-answer</c>.</param>
/// <param name="Net">Rupees after charges: an allowed buy played on the recorded prices, else ₹0.</param>
public sealed record LessonArm(string Action, string Rule, string Text, decimal Net, long? CallId)
{
    /// <summary>No plan to judge: the answer could not be read, or no model answered.</summary>
    public bool Bad => Rule is "unreadable" or "no-answer";
}

/// <summary>One past look asked twice: without the lesson (control) and with it (treatment).</summary>
public sealed record LessonPoint(long DecisionId, DateOnly? Day, string ClockIst, LessonArm Control, LessonArm Treatment);

/// <summary>A lesson's test as it stands: the looks chosen, those asked, and a control answer waiting for its treatment.</summary>
public sealed class LessonTest(long lessonId, DateOnly? sourceDay, IReadOnlyList<long> points, DateTime startedUtc)
{
    public long LessonId { get; } = lessonId;

    /// <summary>The day the lesson was learned from: its looks are never among the points.</summary>
    public DateOnly? SourceDay { get; } = sourceDay;

    public IReadOnlyList<long> Points { get; } = points;

    public List<LessonPoint> Done { get; } = [];

    public DateTime StartedUtc { get; } = startedUtc;

    /// <summary>A look whose control was answered and whose treatment was turned away: asked again without asking the control twice.</summary>
    public (long DecisionId, LessonArm Arm)? PendingControl { get; set; }
}

/// <summary>
/// What the lesson work keeps between scheduler minutes, in memory: the lesson under test and when the provider may be
/// asked again. One per API (a singleton). A restart loses a test under way, which then starts again from its first
/// look: a few model calls, nothing decided.
/// </summary>
public sealed class AiTraderLessonState
{
    /// <summary>One step at a time: the scheduler's minute and "Run now" never ask the same look twice.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public LessonTest? Current { get; set; }

    /// <summary>After a call turned away for capacity, nothing is asked before this.</summary>
    public DateTime PauseUntilUtc { get; set; }
}

/// <summary>The verdict on a lesson's test, from its points alone (<see cref="AiTraderLessonCheck.Judge"/>).</summary>
public sealed record LessonVerdict(
    int Points, decimal ControlNet, decimal TreatmentNet, decimal Gain, int Helped, int Hurt, int ControlBad, int TreatmentBad,
    bool Passed, string Why);

/// <summary>A recorded chain at a moment, as a lesson's test resolves a buy on it.</summary>
public sealed record RecordedChain(decimal Atm, IReadOnlyList<RecordedStrike> Strikes, int LotSize, DateOnly Expiry);

public sealed record RecordedStrike(decimal Strike, string? Call, string? Put);

/// <summary>The recorded market a lesson's test plays buys on (<see cref="RecordedMarket"/>; a fake in tests).</summary>
public interface IRecordedMarket
{
    /// <summary>The nearest expiry's chain as recorded at <paramref name="asOfUtc"/>; null when none was captured that day.</summary>
    Task<RecordedChain?> ChainAsync(string underlying, DateTime asOfUtc, CancellationToken cancellationToken);

    /// <summary>An option's recorded ticks from <paramref name="fromUtc"/> (inclusive) to <paramref name="untilUtc"/>, oldest first.</summary>
    Task<IReadOnlyList<BaselineTick>> TicksAsync(string symbol, DateTime fromUtc, DateTime untilUtc, CancellationToken cancellationToken);
}

/// <summary>
/// The chain as recorded at a moment (only a chain captured that day, as <see cref="BaselineMarket"/> reads it: an
/// earlier day's capture names that day's money, and after an expiry a gone contract) and the recorded ticks.
/// </summary>
public sealed class RecordedMarket(TradingDbContext db, OptionChainService chains) : IRecordedMarket
{
    public async Task<RecordedChain?> ChainAsync(string underlying, DateTime asOfUtc, CancellationToken cancellationToken)
    {
        var view = await chains.GetViewAsync(underlying, null, asOfUtc, cancellationToken);
        if (view.Strikes.Count == 0 || IstTime.DateOf(view.AsOfUtc) != IstTime.DateOf(asOfUtc)) return null;
        decimal? atm = view.AtTheMoneyStrike ?? view.Strikes.FirstOrDefault(s => s.IsAtTheMoney)?.StrikePrice;
        int lot = view.Header?.LotSize ?? 0;
        return atm is not decimal a || a <= 0 || lot <= 0
            ? null
            : new RecordedChain(a, view.Strikes.Select(s => new RecordedStrike(s.StrikePrice, s.Call?.Symbol, s.Put?.Symbol)).ToList(), lot, view.ExpiryDate);
    }

    public async Task<IReadOnlyList<BaselineTick>> TicksAsync(string symbol, DateTime fromUtc, DateTime untilUtc, CancellationToken cancellationToken) =>
        await db.LiveTicks.AsNoTracking()
            .Where(t => t.Symbol == symbol && t.ReceivedUtc >= fromUtc && t.ReceivedUtc < untilUtc && t.SourceKey != "mock")
            .OrderBy(t => t.ReceivedUtc)
            .Select(t => new BaselineTick(t.ReceivedUtc, t.LastTradedPrice, t.BidPrice, t.AskPrice))
            .ToListAsync(cancellationToken);
}

/// <summary>
/// The test of one AI Trader lesson before it is used (owner, 1 Oct: lessons are tested, not approved). The evidence
/// decides; the owner can still take any lesson out.
/// </summary>
/// <remarks>
/// <para>
/// Up to <see cref="AiSettings.AiTraderLessonPoints"/> (16) past looks with a brief and a readable answer, from days
/// other than the one the lesson came from, in the hours a buy may open (09:20–14:45 IST: outside them every buy is
/// refused either way), the most recent days first, spread across days and mixing looks that acted with looks that
/// did nothing. Each look's stored brief is asked twice with the AI Trader's own prompt: CONTROL with the lessons it
/// would read at that moment (active, learned before that look's day), TREATMENT with the lesson on trial added.
/// </para>
/// <para>
/// Each answer is read as a plan and judged by <see cref="AiTraderGuard"/> against an empty book at the look's
/// clock: refused, or anything but a buy, is ₹0. An allowed buy is played on the recorded data: the contract
/// resolved on the chain as recorded at that clock, bought at the ask of the first recorded tick at or after it,
/// each recorded minute's last tick checked at the bid against the plan's stop and target
/// (<see cref="AiTraderBaselineScorer.Walk"/>), squared off at 15:30, after the shadow book's charges. Exits a later
/// look would have made are not played, in either arm.
/// </para>
/// <para>
/// The lesson is used only if, over its looks, TREATMENT's net beats CONTROL's by at least
/// <see cref="AiSettings.AiTraderLessonMinGain"/> (₹500), it helps on at least as many looks as it hurts, and it has no
/// more unreadable or unanswered results; otherwise it is dropped. A lesson that passes when
/// <see cref="AiSettings.AiTraderMaxLessons"/> (8) are active replaces the active one with the weakest evidence (its
/// test's gain; ₹0 for one never tested), or is dropped when it is no stronger. Every look is kept in the test's
/// report (agent <c>ai-trader-lesson-check</c>, subject <c>M{id}</c>).
/// </para>
/// <para>
/// One look (two asks) a scheduler minute, as the assistant exam paces itself: a whole test at once would be 32 asks
/// in a few minutes, past the desk's 30 a user in ten, and would hold every other agent's turn. A call turned away
/// (the rate limit, the provider's capacity, the AI Trader switched off) pauses the test and the same look is asked
/// again; a call no model answered is asked once more, then counts as no answer. A failure never activates a lesson.
/// </para>
/// </remarks>
public sealed class AiTraderLessonCheck(
    TradingDbContext db,
    AiGateway gateway,
    AiReportWriter reports,
    AiMemoryService memory,
    IRecordedMarket market,
    IMarketSessionService sessions,
    AiTraderLessonState state,
    IOptionsMonitor<AiSettings> settings,
    ILogger<AiTraderLessonCheck> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The report's subject for a lesson: <c>M45</c>.</summary>
    public static string SubjectOf(long lessonId) => "M" + lessonId.ToString(CultureInfo.InvariantCulture);

    /// <summary>How long a test waits after a call turned away with no Retry-After of its own.</summary>
    public static readonly TimeSpan PauseAfterRefusal = TimeSpan.FromMinutes(2);

    /// <summary>
    /// One step: the next look of the lesson under test; with <paramref name="startNew"/>, the oldest proposed lesson's
    /// test when none is under way. The verdict once its last look is in. True when it asked or decided something.
    /// </summary>
    public async Task<bool> StepAsync(DateTime nowUtc, bool startNew, CancellationToken cancellationToken)
    {
        if (nowUtc < state.PauseUntilUtc) return false;

        var test = state.Current;
        AiMemory? lesson = null;
        if (test is not null)
        {
            lesson = await db.AiMemories.FirstOrDefaultAsync(m => m.Id == test.LessonId, cancellationToken);
            if (lesson is null || lesson.Status != AiMemoryStatus.Proposed)
            {
                // The owner approved, rejected or edited it out of its test meanwhile: theirs is the decision.
                state.Current = test = null;
            }
        }

        if (test is null)
        {
            if (!startNew) return false;
            lesson = await db.AiMemories
                .Where(m => m.AgentKey == AiCatalog.AiTrader && m.Kind == AiMemoryKind.Lesson && m.Status == AiMemoryStatus.Proposed)
                .OrderBy(m => m.CreatedUtc).ThenBy(m => m.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (lesson is null) return false;
            test = await StartAsync(lesson, nowUtc, cancellationToken);
            state.Current = test;
            logger.LogInformation("AI Trader lesson M{Id}: tested on {Points} past looks", lesson.Id, test.Points.Count);
        }

        if (test.Done.Count < test.Points.Count)
        {
            var point = await AskAsync(test, lesson!, test.Points[test.Done.Count], nowUtc, cancellationToken);
            if (point is null) return true;
            test.Done.Add(point);
            if (test.Done.Count < test.Points.Count) return true;
        }

        await FinishAsync(test, lesson!, cancellationToken);
        state.Current = null;
        return true;
    }

    private async Task<LessonTest> StartAsync(AiMemory lesson, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var days = await new AiTraderMemory(db, settings).SourceDaysAsync([lesson], cancellationToken);
        DateOnly? from = days.TryGetValue(lesson.Id, out var d) ? d : null;
        var rows = await db.AiTraderDecisions.AsNoTracking()
            .Where(x => x.Brief != "" && x.Rule != "no-answer" && x.Rule != "unreadable" && x.CreatedUtc < lesson.CreatedUtc)
            .Where(x => from == null || x.Day != from)
            .Select(x => new LessonCandidate(x.Id, x.Day, x.ClockUtc, x.Action))
            .ToListAsync(cancellationToken);
        return new LessonTest(lesson.Id, from, Pick(rows, settings.CurrentValue.AiTraderLessonPoints, AiTraderAgent.Rules), nowUtc);
    }

    /// <summary>A past look a test may ask: its id, day, clock and the action it took.</summary>
    public sealed record LessonCandidate(long Id, DateOnly Day, DateTime ClockUtc, string Action);

    /// <summary>
    /// The looks a test asks, at most <paramref name="count"/>: only those in the hours a buy may open, the most recent
    /// days first and spread across days (each day's newest, then each day's next), alternating looks that proposed an
    /// action with looks that did nothing, and filling from the other kind when one runs out. Public for tests.
    /// </summary>
    public static IReadOnlyList<long> Pick(IEnumerable<LessonCandidate> candidates, int count, AiTraderRules rules)
    {
        var open = candidates.Where(c =>
        {
            var ist = TimeOnly.FromDateTime(IstTime.ToIst(c.ClockUtc));
            return ist >= rules.OpenFrom && ist <= rules.OpenUntil;
        }).ToList();
        var actions = Spread(open.Where(c => c.Action is not ("" or AiTraderPlan.None)));
        var nones = Spread(open.Where(c => c.Action is "" or AiTraderPlan.None));

        var picked = new List<long>();
        int a = 0, n = 0;
        while (picked.Count < count && (a < actions.Count || n < nones.Count))
        {
            bool actionTurn = picked.Count % 2 == 0;
            if ((actionTurn && a < actions.Count) || n >= nones.Count) picked.Add(actions[a++].Id);
            else picked.Add(nones[n++].Id);
        }

        return picked;

        static List<LessonCandidate> Spread(IEnumerable<LessonCandidate> items) => items
            .GroupBy(c => c.Day)
            .SelectMany(g => g.OrderByDescending(c => c.ClockUtc).ThenByDescending(c => c.Id).Select((c, i) => (c, i)))
            .OrderBy(x => x.i).ThenByDescending(x => x.c.Day)
            .Select(x => x.c)
            .ToList();
    }

    /// <summary>One look asked without and with the lesson. Null when a call was turned away: the same look is asked next time.</summary>
    private async Task<LessonPoint?> AskAsync(LessonTest test, AiMemory lesson, long decisionId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var look = await db.AiTraderDecisions.AsNoTracking().Where(x => x.Id == decisionId)
            .Select(x => new { x.Id, x.Day, x.ClockUtc, x.Brief }).FirstOrDefaultAsync(cancellationToken);
        if (look is null)
        {
            var gone = new LessonArm(string.Empty, "gone", "The look is no longer kept.", 0m, null);
            return new LessonPoint(decisionId, null, string.Empty, gone, gone);
        }

        var control = (await new AiTraderMemory(db, settings).ForAsync(look.ClockUtc, count: false, nowUtc, cancellationToken))
            .Where(m => m.Id != lesson.Id).ToList();
        var treatment = AiTraderMemory.Ordered(control.Append(lesson)).ToList();
        string conversation = $"lesson-{SubjectOf(lesson.Id)}-d{look.Id.ToString(CultureInfo.InvariantCulture)}";

        LessonArm? c = test.PendingControl is { } pending && pending.DecisionId == look.Id
            ? pending.Arm
            : await ArmAsync(look.Brief, look.ClockUtc, control, conversation + "-control", nowUtc, cancellationToken);
        if (c is null) return null;
        test.PendingControl = (look.Id, c);

        var t = await ArmAsync(look.Brief, look.ClockUtc, treatment, conversation + "-treatment", nowUtc, cancellationToken);
        if (t is null) return null;
        test.PendingControl = null;
        return new LessonPoint(look.Id, look.Day, IstTime.ToIst(look.ClockUtc).ToString("HH:mm", CultureInfo.InvariantCulture), c, t);
    }

    /// <summary>
    /// The AI Trader asked on a stored brief with <paramref name="memories"/> in its prompt, its answer judged and played.
    /// Null when the call was turned away (the test pauses); a call no model answered is asked once more.
    /// </summary>
    private async Task<LessonArm?> ArmAsync(string brief, DateTime clockUtc, IReadOnlyList<AiMemory> memories, string conversation,
        DateTime nowUtc, CancellationToken cancellationToken)
    {
        AiAskResult? last = null;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var result = await gateway.AskAsync(new AiAskInput(
                AiCatalog.AiTrader, null, [new AiMessage("user", brief)], AiTraderMemory.SystemPrompt(memories), 4000, 0.2,
                conversation, "check", AiCatalog.AiTraderLessonCheck, null, GivenMemoryIds: memories.Select(m => m.Id).ToList()),
                NullAiStreamSink.Instance, cancellationToken);
            if (result.Outcome == AiCallOutcome.Ok) return await ScoreAsync(result.Text, clockUtc, result.CallId, cancellationToken);

            if (result.Outcome != AiCallOutcome.Failed || AiGateway.IsCapacityRefusal(result))
            {
                // Not the lesson's doing: the desk's rate limit, the provider's room, the switch, or a stop.
                var wait = result.RetryAfterSeconds > 0 ? TimeSpan.FromSeconds(result.RetryAfterSeconds) : PauseAfterRefusal;
                state.PauseUntilUtc = nowUtc + wait;
                logger.LogInformation("AI Trader lesson test: a call was turned away ({Error}); the same look is asked after {Wait}", result.Error, wait);
                return null;
            }

            last = result;
            logger.LogInformation("AI Trader lesson test: no model answered ({Error}){Again}", result.Error, attempt == 1 ? "; asked once more" : string.Empty);
        }

        return new LessonArm(string.Empty, "no-answer", Cut(last?.Error ?? "No model answered.", 300), 0m, last?.CallId);
    }

    /// <summary>
    /// An answer as the test scores it: read as a plan, judged against an empty book at <paramref name="clockUtc"/>, and an
    /// allowed buy played on the recorded data. Public for tests.
    /// </summary>
    public async Task<LessonArm> ScoreAsync(string answer, DateTime clockUtc, long? callId, CancellationToken cancellationToken)
    {
        var (plan, error) = AiTraderPlanReader.Read(answer);
        if (plan is null) return new LessonArm(string.Empty, "unreadable", Cut(error ?? "The answer was not a plan.", 300), 0m, callId);

        var book = new AiTraderBook(clockUtc, sessions.GetSessionInfo(clockUtc, "NSE", "FO").IsTradingDay, false, 0m, 0, [], []);
        var played = plan.Action == AiTraderPlan.Buy ? await PlayAsync(plan, clockUtc, cancellationToken) : null;
        var verdict = AiTraderGuard.Check(plan, book, AiTraderAgent.Rules, played?.Contract);
        string what = plan.Action == AiTraderPlan.Buy ? AiTraderAgent.BuyText(JsonSerializer.Serialize(plan, Json)) : $"{plan.Action} {plan.Underlying}".TrimEnd();
        if (!verdict.Allowed) return new LessonArm(plan.Action, verdict.Rule, $"{what}: refused ({verdict.Rule})", 0m, callId);
        if (plan.Action != AiTraderPlan.Buy || played is null)
        {
            return new LessonArm(plan.Action, verdict.Rule, plan.Action == AiTraderPlan.None ? "none" : $"{what}: not played (the test starts each look from an empty book)", 0m, callId);
        }

        var (contract, entry, ticks) = played;
        var (exitAt, exitPrice, reason) = AiTraderBaselineScorer.Walk(ticks.Where(x => x.AtUtc > entry.AtUtc).ToList(), plan.StopLoss!.Value, plan.Target!.Value)
                                          ?? (entry.AtUtc, contract.Ask, AiTraderShadowBook.SessionClose);
        int units = plan.Lots!.Value * contract.LotSize;
        decimal charges = AiTraderShadowBook.Charges(contract.Symbol, contract.Ask, exitPrice, units);
        decimal net = Math.Round((exitPrice - contract.Ask) * units - charges, 2);
        string text = string.Create(CultureInfo.InvariantCulture,
            $"buy {contract.Symbol} ×{plan.Lots}: {IstTime.ToIst(entry.AtUtc):HH:mm} at {contract.Ask:0.##} → {IstTime.ToIst(exitAt):HH:mm} at {exitPrice:0.##} by its {reason}");
        return new LessonArm(plan.Action, verdict.Rule, text, net, callId);
    }

    private sealed record Played(AiTraderContract Contract, BaselineTick Entry, IReadOnlyList<BaselineTick> Ticks);

    /// <summary>
    /// The contract a buy names on the chain as recorded at <paramref name="clockUtc"/>, priced at the ask of the first
    /// recorded tick at or after it (else its last trade), with the ticks to the close. Null when there is none.
    /// </summary>
    private async Task<Played?> PlayAsync(AiTraderPlan plan, DateTime clockUtc, CancellationToken cancellationToken)
    {
        if (plan.Underlying is null || plan.Option is not ("CE" or "PE")) return null;
        var chain = await market.ChainAsync(plan.Underlying, clockUtc, cancellationToken);
        if (chain is null) return null;
        var strikes = chain.Strikes.Select(s => s.Strike).Distinct().OrderBy(s => s).ToList();
        if (strikes.Count < 2) return null;
        decimal step = strikes.Zip(strikes.Skip(1), (a, b) => b - a).Where(x => x > 0).DefaultIfEmpty(0m).Min();
        if (AiTraderPlanReader.StrikeOf(plan.Strike, chain.Atm, step) is not decimal strike) return null;
        var row = chain.Strikes.FirstOrDefault(s => s.Strike == strike);
        string? symbol = plan.Option == "CE" ? row?.Call : row?.Put;
        if (string.IsNullOrWhiteSpace(symbol)) return null;

        var close = sessions.GetSessionInfo(clockUtc, "NSE", "FO").SessionCloseUtc;
        var ticks = await market.TicksAsync(symbol, clockUtc, close, cancellationToken);
        var entry = ticks.FirstOrDefault(t => t.Ask is > 0 || t.Last is > 0);
        if (entry is null) return null;
        decimal price = entry.Ask is > 0 ? entry.Ask.Value : entry.Last!.Value;
        return new Played(new AiTraderContract(symbol, plan.Underlying, plan.Option, strike, chain.Expiry, price, chain.LotSize), entry, ticks);
    }

    /// <summary>
    /// The verdict from a test's looks: used when TREATMENT's net beats CONTROL's by at least <paramref name="minGain"/>,
    /// it helps on at least as many looks as it hurts, and it has no more unreadable or unanswered results. Public for tests.
    /// </summary>
    public static LessonVerdict Judge(IReadOnlyList<LessonPoint> points, decimal minGain)
    {
        decimal control = points.Sum(p => p.Control.Net);
        decimal treatment = points.Sum(p => p.Treatment.Net);
        decimal gain = treatment - control;
        int helped = points.Count(p => p.Treatment.Net > p.Control.Net);
        int hurt = points.Count(p => p.Treatment.Net < p.Control.Net);
        int controlBad = points.Count(p => p.Control.Bad);
        int treatmentBad = points.Count(p => p.Treatment.Bad);
        string over = $"over {points.Count} {(points.Count == 1 ? "look" : "looks")}";

        string why = gain < minGain ? $"check: {AiTraderReflection.Rupees(gain)} with it {over}, under the {AiTraderReflection.Rupees(minGain)} needed"
            : helped < hurt ? $"check: {AiTraderReflection.Rupees(gain)} {over}, but it hurt {hurt} and helped {helped}"
            : treatmentBad > controlBad ? $"check: {treatmentBad} unreadable or unanswered with it, {controlBad} without"
            : $"check: {AiTraderReflection.Rupees(gain)} {over}; helped {helped}, hurt {hurt}";
        bool passed = gain >= minGain && helped >= hurt && treatmentBad <= controlBad;
        return new LessonVerdict(points.Count, control, treatment, gain, helped, hurt, controlBad, treatmentBad, passed, Cut(why, 100));
    }

    private async Task FinishAsync(LessonTest test, AiMemory lesson, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        var verdict = Judge(test.Done, s.AiTraderLessonMinGain);
        string decided = verdict.Why;
        bool use = verdict.Passed;
        var retire = new List<AiMemory>();

        if (use)
        {
            // At most AiTraderMaxLessons active: the weakest make way for a stronger one, or it is not used.
            var active = await db.AiMemories
                .Where(m => m.AgentKey == AiCatalog.AiTrader && m.Kind == AiMemoryKind.Lesson && m.Status == AiMemoryStatus.Active && m.Id != lesson.Id)
                .ToListAsync(cancellationToken);
            int over = active.Count + 1 - Math.Max(1, s.AiTraderMaxLessons);
            if (over > 0)
            {
                var gains = await GainsAsync(active.Select(m => m.Id).ToList(), cancellationToken);
                var weakest = active.OrderBy(m => gains.GetValueOrDefault(m.Id)).ThenBy(m => m.ActivatedUtc ?? m.CreatedUtc).Take(over).ToList();
                if (weakest.Any(m => gains.GetValueOrDefault(m.Id) >= verdict.Gain))
                {
                    use = false;
                    decided = Cut($"check: passed ({AiTraderReflection.Rupees(verdict.Gain)}), but no stronger than the {active.Count} active lessons", 100);
                }
                else
                {
                    retire = weakest;
                }
            }
        }

        string title = $"{SubjectOf(lesson.Id)} tested on {verdict.Points} past {(verdict.Points == 1 ? "look" : "looks")}: " +
                       $"{AiTraderReflection.Rupees(verdict.Gain)} with it (helped {verdict.Helped}, hurt {verdict.Hurt}); {(use ? "used" : "not used")}";
        await reports.SaveAsync(AiCatalog.AiTraderLessonCheck, AiReportSubject.Check, SubjectOf(lesson.Id), test.SourceDay, AiReportStatus.Ok, null,
            title, Body(lesson, test, verdict, use, decided, s.AiTraderLessonMinGain), Data(lesson, test, verdict, use, decided, s.AiTraderLessonMinGain),
            string.Empty, cancellationToken);

        await memory.UpdateAsync(lesson.Id, null, use ? AiMemoryStatus.Active : AiMemoryStatus.Rejected, decided, cancellationToken);
        foreach (var old in retire)
        {
            await memory.UpdateAsync(old.Id, null, AiMemoryStatus.Retired, $"check: made way for {SubjectOf(lesson.Id)}, which tested stronger", cancellationToken);
        }

        logger.LogInformation("AI Trader lesson M{Id}: {Verdict} ({Why}){Retired}", lesson.Id, use ? "used" : "not used", decided,
            retire.Count > 0 ? $"; retired {string.Join(", ", retire.Select(r => SubjectOf(r.Id)))}" : string.Empty);
    }

    /// <summary>Each lesson's evidence: its test's gain, from its report. A lesson never tested has none and counts as ₹0.</summary>
    public async Task<Dictionary<long, decimal>> GainsAsync(IReadOnlyCollection<long> lessonIds, CancellationToken cancellationToken)
    {
        var subjects = lessonIds.Select(SubjectOf).ToList();
        var rows = await db.AiReports.AsNoTracking()
            .Where(r => r.AgentKey == AiCatalog.AiTraderLessonCheck && r.SubjectType == AiReportSubject.Check && subjects.Contains(r.SubjectId))
            .Select(r => new { r.SubjectId, r.DataJson })
            .ToListAsync(cancellationToken);
        var gains = new Dictionary<long, decimal>();
        foreach (var r in rows)
        {
            if (long.TryParse(r.SubjectId.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && GainOf(r.DataJson) is decimal g)
            {
                gains[id] = g;
            }
        }

        return gains;
    }

    /// <summary>The gain a test's report data holds; null when it holds none.</summary>
    public static decimal? GainOf(string dataJson)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson) is JsonObject o && o["gain"] is JsonValue v && v.TryGetValue(out decimal g) ? g : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Body(AiMemory lesson, LessonTest test, LessonVerdict v, bool used, string decided, decimal minGain)
    {
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"**{SubjectOf(lesson.Id)}**: {lesson.Text}\n\n");
        string from = test.SourceDay is DateOnly d ? $", none from {d.ToString("d MMM", CultureInfo.InvariantCulture)}, the day it came from" : string.Empty;
        b.Append(CultureInfo.InvariantCulture,
            $"Asked on {v.Points} past {(v.Points == 1 ? "look" : "looks")}{from}, each twice with the AI Trader's own prompt: without it (the lessons it read then) and with it. ");
        b.Append("Each answer is judged by the rules against an empty book at the look's time; an allowed buy is played on the recorded prices ")
            .Append("(in at the ask of the first tick after the look, out at its stop, its target or 15:30, after charges); anything else is ₹0.\n\n");
        b.Append("| Look | Day | Without it | Net | With it | Net |\n|---|---|---|---|---|---|\n");
        foreach (var p in test.Done)
        {
            string day = p.Day is DateOnly pd ? pd.ToString("d MMM", CultureInfo.InvariantCulture) + " " + p.ClockIst : "—";
            b.Append(CultureInfo.InvariantCulture,
                $"| #{p.DecisionId} | {day} | {Cell(ArmText(p.Control))} | {AiTraderReflection.Rupees(p.Control.Net)} | {Cell(ArmText(p.Treatment))} | {AiTraderReflection.Rupees(p.Treatment.Net)} |\n");
        }

        b.Append(CultureInfo.InvariantCulture,
            $"\n**Without it {AiTraderReflection.Rupees(v.ControlNet)} · with it {AiTraderReflection.Rupees(v.TreatmentNet)} · difference {AiTraderReflection.Rupees(v.Gain)}.** ");
        b.Append(CultureInfo.InvariantCulture, $"Helped {v.Helped}, hurt {v.Hurt}; unreadable or unanswered {v.ControlBad} without it, {v.TreatmentBad} with it.\n\n");
        b.Append(used ? "**Used**" : "**Not used**").Append(CultureInfo.InvariantCulture,
            $": {decided.Replace("check: ", string.Empty)}. It is used only with a difference of {AiTraderReflection.Rupees(minGain)} or more, as many looks helped as hurt, and no more unreadable answers.\n");
        return b.ToString();
    }

    private static string ArmText(LessonArm a) => a.Rule switch
    {
        "unreadable" => "unreadable answer",
        "no-answer" => "no answer",
        _ => a.Text,
    };

    private static string Data(AiMemory lesson, LessonTest test, LessonVerdict v, bool used, string decided, decimal minGain) =>
        JsonSerializer.Serialize(new
        {
            lessonId = lesson.Id,
            text = lesson.Text,
            sourceDay = test.SourceDay?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            points = test.Done.Select(p => new
            {
                decisionId = p.DecisionId,
                day = p.Day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                clockIst = p.ClockIst,
                control = Arm(p.Control),
                treatment = Arm(p.Treatment),
            }),
            control = new { net = v.ControlNet, bad = v.ControlBad },
            treatment = new { net = v.TreatmentNet, bad = v.TreatmentBad },
            gain = v.Gain,
            helped = v.Helped,
            hurt = v.Hurt,
            minGain,
            passed = v.Passed,
            used,
            verdict = decided,
        }, Json);

    private static object Arm(LessonArm a) => new { action = a.Action, rule = a.Rule, text = a.Text, net = a.Net, callId = a.CallId };

    private static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
