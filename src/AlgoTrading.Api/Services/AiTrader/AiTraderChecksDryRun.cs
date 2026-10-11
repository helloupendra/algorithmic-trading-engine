using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// How a rehearsal of the AI Trader's checks asks (<see cref="AiTraderChecksDryRun"/>). The Validator and the Risk Memo
/// build their own questions and read the answers with their own code; only the sending differs. Each question goes
/// as source <see cref="Source"/>, by the admin who asked (their rate limit pays, not the agents'), on the chain they
/// forced or else the agent's own, with the agent's memories read but not counted as used. It waits its turn under the
/// desk's rate limit rather than be refused, and keeps every answer, so each can be shown beside what the check made
/// of it.
/// </summary>
public sealed class AiTraderChecksRehearsal(
    string requestedBy, long? userId, IReadOnlyList<string>? chain, AiRateLimiter? limiter = null, TimeProvider? time = null)
{
    /// <summary>The source its calls carry on the Calls tab.</summary>
    public const string Source = "dry-run";

    /// <summary>Refusals by the desk's own rate limit an ask waits out before it counts as unanswered.</summary>
    private const int MaxRefusals = 3;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<AiTraderRehearsedAsk> _asks = [];

    /// <summary>Every ask in the order it was sent: one per Validator sample, one for a memo. Null when the ask threw.</summary>
    public IReadOnlyList<AiTraderRehearsedAsk> Asks => _asks;

    /// <summary>A check's question to the gateway: as built, live; through <paramref name="rehearsal"/> when there is one.</summary>
    internal static Task<AiAskResult> AskAsync(AiGateway gateway, AiAskInput input, AiTraderChecksRehearsal? rehearsal,
        CancellationToken cancellationToken) =>
        rehearsal is null
            ? gateway.AskAsync(input, NullAiStreamSink.Instance, cancellationToken)
            : rehearsal.AskAsync(gateway, input, cancellationToken);

    /// <summary>A check's own question as this rehearsal sends it: who asks, the source, the chain, memories not counted.</summary>
    public AiAskInput Recast(AiAskInput live) => live with
    {
        Source = Source,
        RequestedBy = requestedBy,
        UserId = userId,
        ConversationId = Cut($"{Source}-{live.ConversationId}", 64),
        ForcedChain = chain is { Count: > 0 } ? chain : null,
        CountMemoryUses = false,
    };

    private async Task<AiAskResult> AskAsync(AiGateway gateway, AiAskInput live, CancellationToken cancellationToken)
    {
        var input = Recast(live);
        var waited = TimeSpan.Zero;
        try
        {
            for (int refused = 0; ; refused++)
            {
                var turn = limiter?.WaitFor(requestedBy) ?? TimeSpan.Zero;
                if (turn > TimeSpan.Zero)
                {
                    waited += turn;
                    await Task.Delay(turn, _time, cancellationToken);
                }

                var result = await gateway.AskAsync(input, NullAiStreamSink.Instance, cancellationToken);
                // The desk's own brake (a busy minute beside this rehearsal's burst): the model was never asked, so wait and ask again.
                if (result.Outcome == AiCallOutcome.Refused && result.RefusalStatus == 429 && refused < MaxRefusals)
                {
                    var pause = TimeSpan.FromSeconds(Math.Max(1, result.RetryAfterSeconds));
                    waited += pause;
                    await Task.Delay(pause, _time, cancellationToken);
                    continue;
                }

                _asks.Add(new AiTraderRehearsedAsk(result, waited));
                return result;
            }
        }
        catch
        {
            // An ask that threw is still one of the asks, so the answers stay in step with the check's samples.
            _asks.Add(new AiTraderRehearsedAsk(null, waited));
            throw;
        }
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>One ask of a rehearsal: the gateway's answer (null when the ask threw) and how long it waited its turn.</summary>
public sealed record AiTraderRehearsedAsk(AiAskResult? Result, TimeSpan Waited);

/// <summary>
/// A rehearsal of the checks before a live buy or strategy start (owner, 11 Oct 2026, the day before the AI Trader's
/// first live paper orders): the Validator and the Risk Memo asked on past plans the rules allowed, as a live placement
/// asks them, with nothing placed or recorded. It answers whether the judge tier's answers can be read, and what a live
/// day would have placed, before a live day depends on it.
/// </summary>
/// <remarks>
/// <para>
/// Each decision is rebuilt as the live path hands it to the checks: the plan from its <c>PlanJson</c>, taken as a live
/// placement takes it (<see cref="AiTraderAgent.ForChecks"/>); the contract and price its look resolved, kept in its
/// result; the rules' verdict as recorded; the brief it read. The Validator and the Risk Memo then run their own code
/// through a <see cref="AiTraderChecksRehearsal"/>: the same prompts, three sampled asks, the same reading, quote check
/// and tally, the same tightening. The memo reads the AI Trader's account as it is now, as a live memo does, and is
/// asked whatever the Validator said, so its reading is measured on every plan; a live day asks it only after an accept
/// (<see cref="AiTraderDryRunMemo.AskedLive"/>).
/// </para>
/// <para>
/// It writes nothing to the desk: no order, position or run, no <c>ai_reports</c> row, no change to a decision (each is
/// read untracked). Its model calls are rows on the Calls tab, source <c>dry-run</c>, by the admin who asked.
/// </para>
/// </remarks>
public sealed class AiTraderChecksDryRun(
    TradingDbContext db,
    AiTraderValidator validator,
    AiTraderRiskMemo riskMemo,
    IAiTraderBooks books,
    AiRateLimiter limiter,
    ILogger<AiTraderChecksDryRun> logger,
    TimeProvider? time = null)
{
    /// <summary>Decisions in one dry run: each is four model calls.</summary>
    public const int MaxDecisions = 20;

    /// <summary>The outcome of a plan the Validator let through to the Risk Memo and the rules' last check.</summary>
    public const string Placed = "placed";

    public const string Unreadable = "unreadable";

    public const string NoAnswer = "no-answer";

    private const int ExcerptLength = 600;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>What is wrong with a request, in words; null when it can be run. The chain is the controller's to check.</summary>
    public static string? Problem(IReadOnlyList<long>? decisionIds, int? take)
    {
        bool ids = decisionIds is { Count: > 0 };
        if (ids && take is not null) return "Give decisionIds or take, not both.";
        if (!ids && take is null) return "Give decisionIds (the decisions to rehearse) or take (the most recent buys and starts the rules allowed).";
        if (ids && decisionIds!.Distinct().Count() > MaxDecisions)
        {
            return $"At most {MaxDecisions} decisions in one dry run: each is four model calls.";
        }

        return take is < 1 ? $"take is 1 to {MaxDecisions}." : null;
    }

    /// <summary>
    /// Rehearses the checks on <paramref name="decisionIds"/>, or on the <paramref name="take"/> most recent buys and
    /// strategy starts the rules allowed (any mode, replays included; at most <see cref="MaxDecisions"/>).
    /// </summary>
    public async Task<AiTraderChecksDryRunResult> RunAsync(IReadOnlyList<long>? decisionIds, int? take, IReadOnlyList<string>? chain,
        string requestedBy, long? userId, CancellationToken cancellationToken)
    {
        if (Problem(decisionIds, take) is string problem) throw new ArgumentException(problem);
        var clock = Stopwatch.StartNew();
        chain = chain is { Count: > 0 } ? chain : null;

        IReadOnlyList<(long Id, AiTraderDecision? Row)> picked;
        int requested;
        bool capped = false;
        if (decisionIds is { Count: > 0 })
        {
            var ids = decisionIds.Distinct().ToList();
            var rows = await db.AiTraderDecisions.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, cancellationToken);
            picked = ids.Select(id => (id, rows.GetValueOrDefault(id))).ToList();
            requested = ids.Count;
        }
        else
        {
            requested = take!.Value;
            capped = requested > MaxDecisions;
            var rows = await db.AiTraderDecisions.AsNoTracking()
                .Where(d => (d.Action == AiTraderPlan.Buy || d.Action == AiTraderPlan.StartStrategy) && d.Allowed)
                .OrderByDescending(d => d.Id)
                .Take(Math.Min(requested, MaxDecisions))
                .ToListAsync(cancellationToken);
            picked = rows.Select(d => (d.Id, (AiTraderDecision?)d)).ToList();
        }

        var decisions = new List<AiTraderDryRunDecision>();
        var waited = TimeSpan.Zero;
        foreach (var (id, row) in picked)
        {
            if (row is null)
            {
                decisions.Add(AiTraderDryRunDecision.Skip(id, null, $"No decision {id}."));
                continue;
            }

            var (decision, wait) = await RehearseAsync(row, chain, requestedBy, userId, cancellationToken);
            decisions.Add(decision);
            waited += wait;
        }

        var summary = Summarise(decisions, clock.Elapsed, waited);
        logger.LogInformation(
            "AI Trader checks dry run by {By}: {Rehearsed} decisions, {Parsed} of {Answers} Validator answers read, {Placed} would be placed, {Memos} of {MemoCount} memos read",
            requestedBy, summary.Rehearsed, summary.ValidatorParsed, summary.ValidatorAnswers, summary.WouldPlace, summary.RiskMemosParsed, summary.RiskMemos);

        string note = "A rehearsal: nothing was placed or recorded (no order, position, run or report, and no decision changed). Its model calls " +
                      $"are on AI → Calls, source {AiTraderChecksRehearsal.Source}. \"{Placed}\" is a plan the Validator let through to the Risk Memo " +
                      "and the rules' last check on the fresh book." +
                      (capped ? $" take {requested} was capped at {MaxDecisions}." : string.Empty);
        return new AiTraderChecksDryRunResult(note, chain, requested, capped, summary, decisions);
    }

    /// <summary>One decision: rebuilt as the live path hands it to the checks, then the Validator and the Risk Memo asked.</summary>
    private async Task<(AiTraderDryRunDecision Decision, TimeSpan Waited)> RehearseAsync(AiTraderDecision row, IReadOnlyList<string>? chain,
        string requestedBy, long? userId, CancellationToken cancellationToken)
    {
        if (row.Action is not (AiTraderPlan.Buy or AiTraderPlan.StartStrategy))
        {
            return (AiTraderDryRunDecision.Skip(row.Id, row,
                $"It is a \"{row.Action}\" decision: the checks are asked only before a buy or a strategy start."), TimeSpan.Zero);
        }

        if (!row.Allowed)
        {
            return (AiTraderDryRunDecision.Skip(row.Id, row,
                $"The rules did not allow it (rule {row.Rule}): the checks are asked only on a plan the rules allowed."), TimeSpan.Zero);
        }

        var plan = ReadPlan(row.PlanJson);
        if (plan is null || plan.Action != row.Action)
        {
            return (AiTraderDryRunDecision.Skip(row.Id, row, "Its plan could not be read back from its PlanJson."), TimeSpan.Zero);
        }

        AiTraderContract? contract = null;
        if (plan.Action == AiTraderPlan.Buy && (contract = ReadContract(row.ResultJson)) is null)
        {
            return (AiTraderDryRunDecision.Skip(row.Id, row, "No contract is recorded on this buy, and the Validator is told the contract and its price."),
                TimeSpan.Zero);
        }

        plan = AiTraderAgent.ForChecks(plan);
        var verdict = new AiTraderVerdict(row.Allowed, row.Rule, row.Why);

        var judging = new AiTraderChecksRehearsal(requestedBy, userId, chain, limiter, _time);
        var validation = await validator.JudgeAsync(row, plan, contract, verdict, judging, cancellationToken);
        string source = AiTraderValidator.Source(plan, contract, verdict, row.Brief);
        var answers = validation.Samples
            .Select((sample, i) => Answer(i + 1, sample, i < judging.Asks.Count ? judging.Asks[i].Result : null, source))
            .ToList();
        var judged = new AiTraderDryRunValidator(validation.Placeable ? Placed : validation.Rule, validation.Why, validation.Accepts,
            validation.Rejects, validation.NoAnswers, answers);

        var memoing = new AiTraderChecksRehearsal(requestedBy, userId, chain, limiter, _time);
        AiTraderBook? book = null;
        string? unread = null;
        try
        {
            // Its account as a live memo reads it: now, not as of the decision.
            book = await books.ReadAsync(_time.GetUtcNow().UtcDateTime, replay: false, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "AI Trader checks dry run: its account could not be read for the memo on decision {DecisionId}", row.Id);
            unread = ex.Message;
        }

        AiTraderDryRunMemo memoView;
        if (book is null)
        {
            memoView = AiTraderDryRunMemo.NotAsked(validation.Placeable,
                $"Its account could not be read, so the memo was not asked (live, the placement would fail the same way): {unread}");
        }
        else
        {
            var memo = await riskMemo.ReviewAsync(row, plan, contract, verdict, book, AiTraderAgent.Rules, memoing, cancellationToken);
            memoView = Memo(validation.Placeable, memo, memoing.Asks.Count > 0 ? memoing.Asks[0].Result : null);
        }

        var costs = judging.Asks.Concat(memoing.Asks).Select(a => a.Result?.CostUsd).OfType<decimal>().ToList();
        var waited = judging.Asks.Concat(memoing.Asks).Aggregate(TimeSpan.Zero, (sum, a) => sum + a.Waited);
        return (AiTraderDryRunDecision.Of(row) with
        {
            Plan = plan,
            Contract = contract,
            Validator = judged,
            RiskMemo = memoView,
            CostUsd = costs.Count > 0 ? costs.Sum() : null,
        }, waited);
    }

    private static AiTraderPlan? ReadPlan(string planJson)
    {
        try
        {
            return string.IsNullOrWhiteSpace(planJson) ? null : JsonSerializer.Deserialize<AiTraderPlan>(planJson, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The contract a buy resolved to, as its decision's result keeps it (<c>contract</c>).</summary>
    private static AiTraderContract? ReadContract(string resultJson)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(resultJson) || JsonNode.Parse(resultJson)?["contract"] is not JsonObject contract) return null;
            var read = contract.Deserialize<AiTraderContract>(Json);
            return read is { Symbol.Length: > 0, Ask: > 0, LotSize: > 0 } ? read : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One Validator answer beside what the Validator made of it.</summary>
    private static AiTraderDryRunAnswer Answer(int ask, AiTraderValidatorSample sample, AiAskResult? result, string source)
    {
        bool answered = result is { Outcome: AiCallOutcome.Ok };
        return new AiTraderDryRunAnswer(
            ask, result?.CallId, ModelOf(result), result?.Seconds ?? 0, VerdictOf(sample, result), sample.Verdict is not null, sample.Flaw, sample.Quote,
            sample.Quote is { Length: > 0 } quote ? AiTraderValidator.Quotes(source, quote) : null, sample.Confidence, sample.Error,
            sample.Verdict is null && answered ? Cut(result!.Text, ExcerptLength) : null, result?.CostUsd, Attempts(result));
    }

    /// <summary>
    /// The verdict an answer gave: the one the Validator counted; else ACCEPT or REJECT when the JSON said so but it was not
    /// counted (a REJECT with no flaw, or a quote not in the brief); else unreadable; or no answer when no model answered.
    /// </summary>
    private static string VerdictOf(AiTraderValidatorSample sample, AiAskResult? result)
    {
        if (sample.Verdict is not null) return sample.Verdict;
        if (result is not { Outcome: AiCallOutcome.Ok }) return NoAnswer;
        var obj = AiJson.Object(result.Text);
        string? said = obj is null ? null : AiJson.Str(obj, "verdict")?.ToUpperInvariant();
        return said is "ACCEPT" or "REJECT" ? said : Unreadable;
    }

    /// <summary>The memo beside what code would do with it.</summary>
    private static AiTraderDryRunMemo Memo(bool askedLive, AiTraderMemo memo, AiAskResult? result)
    {
        bool answered = result is { Outcome: AiCallOutcome.Ok };
        bool parsed = memo.Status == AiReportStatus.Ok;
        var obj = answered ? AiJson.Object(result!.Text) : null;
        var proposed = obj is null ? null : new AiTraderDryRunProposed(AiJson.Num(obj, "lots"), AiJson.Num(obj, "stopLoss"), AiJson.Num(obj, "target"));
        return new AiTraderDryRunMemo(
            askedLive, result?.CallId, ModelOf(result), result?.Seconds ?? 0, memo.Status, parsed, parsed ? memo.Text : null, memo.Why, proposed,
            memo.Applied, memo.Ignored, memo.Plan.Lots, memo.Plan.StopLoss, memo.Plan.Target, memo.Error,
            !parsed && answered ? Cut(result!.Text, ExcerptLength) : null, result?.CostUsd, Attempts(result));
    }

    private static string? ModelOf(AiAskResult? result) => result?.Model is { Length: > 0 } model ? model : null;

    private static IReadOnlyList<AiTraderDryRunAttempt> Attempts(AiAskResult? result) =>
        result?.Attempts.Select(a => new AiTraderDryRunAttempt(a.Model, a.Outcome, a.Seconds)).ToList() ?? [];

    private static AiTraderDryRunSummary Summarise(IReadOnlyList<AiTraderDryRunDecision> decisions, TimeSpan took, TimeSpan waited)
    {
        var rehearsed = decisions.Where(d => d.Validator is not null).ToList();
        var answers = rehearsed.SelectMany(d => d.Validator!.Answers).ToList();
        var memos = rehearsed.Select(d => d.RiskMemo).OfType<AiTraderDryRunMemo>().Where(m => m.Status != AiTraderDryRunMemo.NotAskedStatus).ToList();
        int parsed = answers.Count(a => a.Verdict is "ACCEPT" or "REJECT");
        int counted = answers.Count(a => a.Counted);
        int placed = rehearsed.Count(d => d.Validator!.Outcome == Placed);
        int memosParsed = memos.Count(m => m.Parsed);

        var attempts = answers.SelectMany(a => a.Attempts).Concat(memos.SelectMany(m => m.Attempts)).ToList();
        var models = attempts.Select(a => a.Model).Concat(answers.Select(a => a.Model)).Concat(memos.Select(m => m.Model)).OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Select(model =>
            {
                var ok = attempts.Where(a => a.Model == model && a.Outcome == "ok").ToList();
                return new AiTraderDryRunModel(
                    model,
                    answers.Count(a => a.Model == model), answers.Count(a => a.Model == model && (a.Verdict is "ACCEPT" or "REJECT")),
                    memos.Count(m => m.Model == model), memos.Count(m => m.Model == model && m.Parsed),
                    attempts.Count(a => a.Model == model && a.Outcome is not ("ok" or "cancelled") && !AiGateway.IsSkipped(a.Outcome)),
                    ok.Count > 0 ? Math.Round(ok.Average(a => a.Seconds), 2) : null);
            })
            .OrderByDescending(m => m.ValidatorAnswers + m.RiskMemos).ThenBy(m => m.Model, StringComparer.Ordinal)
            .ToList();

        var costs = decisions.Select(d => d.CostUsd).OfType<decimal>().ToList();
        return new AiTraderDryRunSummary(
            decisions.Count, rehearsed.Count, decisions.Count - rehearsed.Count,
            answers.Count, parsed, Share(parsed, answers.Count), counted, Share(counted, answers.Count),
            placed, Share(placed, rehearsed.Count), rehearsed.Count(d => d.Validator!.Outcome == AiTraderValidator.RejectedRule),
            rehearsed.Count(d => d.Validator!.Outcome == AiTraderValidator.UnavailableRule),
            memos.Count, memosParsed, Share(memosParsed, memos.Count),
            models, costs.Count > 0 ? costs.Sum() : null, Math.Round(took.TotalSeconds, 1), Math.Round(waited.TotalSeconds, 1));
    }

    private static double? Share(int part, int whole) => whole == 0 ? null : Math.Round((double)part / whole, 3);

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>
/// A dry run's answer: a note on what it is, the chain it forced (null: each agent's own), how many decisions were asked
/// for (<c>Capped</c> when <c>take</c> was over the cap), the summary, and each decision.
/// </summary>
public sealed record AiTraderChecksDryRunResult(string Note, IReadOnlyList<string>? Chain, int Requested, bool Capped,
    AiTraderDryRunSummary Summary, IReadOnlyList<AiTraderDryRunDecision> Decisions);

/// <summary>
/// Over the decisions rehearsed: the Validator's answers and how many were read (<c>Parsed</c>: the JSON asked for with
/// ACCEPT or REJECT) and counted (as the tally counts them: a REJECT needs a flaw and a quote found in the brief); the
/// decisions that would be placed, blocked (<c>validator</c>) or given no verdict (<c>validator-unavailable</c>); the
/// memos read; each model's share; the cost where priced; the seconds it took, and those spent waiting for the rate limit.
/// </summary>
public sealed record AiTraderDryRunSummary(
    int Decisions, int Rehearsed, int Skipped,
    int ValidatorAnswers, int ValidatorParsed, double? ValidatorParsedShare, int ValidatorCounted, double? ValidatorCountedShare,
    int WouldPlace, double? WouldPlaceShare, int BlockedByValidator, int ValidatorUnavailable,
    int RiskMemos, int RiskMemosParsed, double? RiskMemoParsedShare,
    IReadOnlyList<AiTraderDryRunModel> Models, decimal? CostUsd, double Seconds, double WaitedSeconds);

/// <summary>
/// One model across the dry run: the Validator answers and memos it gave and how many were read, its failed attempts
/// (another model then asked, or none answered), and its average seconds when it answered.
/// </summary>
public sealed record AiTraderDryRunModel(string Model, int ValidatorAnswers, int ValidatorParsed, int RiskMemos, int RiskMemosParsed, int Failures,
    double? AverageSeconds);

/// <summary>
/// One decision rehearsed, or skipped with the reason. <c>Plan</c> is the plan the checks were asked on (a start's lots
/// as a live start takes them), <c>Contract</c> the contract and price they were told.
/// </summary>
public sealed record AiTraderDryRunDecision(
    long DecisionId, string? Day, string? ClockIst, string? Mode, long? ReplaySessionId, string? Underlying, string? Action, string? Skipped)
{
    public AiTraderPlan? Plan { get; init; }

    public AiTraderContract? Contract { get; init; }

    public AiTraderDryRunValidator? Validator { get; init; }

    public AiTraderDryRunMemo? RiskMemo { get; init; }

    /// <summary>The decision's calls' cost in US dollars at list price; null when none was priced.</summary>
    public decimal? CostUsd { get; init; }

    public static AiTraderDryRunDecision Of(AiTraderDecision d) => new(
        d.Id, d.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), IstTime.ToIst(d.ClockUtc).ToString("HH:mm", CultureInfo.InvariantCulture),
        d.Mode, d.ReplaySessionId, d.Underlying, d.Action, null);

    public static AiTraderDryRunDecision Skip(long id, AiTraderDecision? d, string why) =>
        d is null ? new(id, null, null, null, null, null, null, why) : Of(d) with { Skipped = why };
}

/// <summary>
/// The Validator on one plan. <c>Outcome</c> is as a live placement reads it: <c>placed</c> (two ACCEPTs), <c>validator</c>
/// (two REJECTs: blocked with the flaw) or <c>validator-unavailable</c> (no majority: blocked, the desk fails closed).
/// </summary>
public sealed record AiTraderDryRunValidator(string Outcome, string Why, int Accepts, int Rejects, int NoVerdict, IReadOnlyList<AiTraderDryRunAnswer> Answers);

/// <summary>
/// One of the Validator's three answers. <c>Verdict</c> is ACCEPT, REJECT, <c>unreadable</c> (not the JSON asked for, or
/// no ACCEPT or REJECT in it) or <c>no-answer</c> (no model answered); <c>Counted</c> is whether the tally counted it, and
/// <c>Error</c> why not. <c>QuoteFound</c> is whether its quote is in the plan's facts or the brief. <c>Excerpt</c> is the
/// start of an answer that was not counted. <c>Seconds</c> is the whole call's, fallbacks included.
/// </summary>
public sealed record AiTraderDryRunAnswer(
    int Ask, long? CallId, string? Model, double Seconds, string Verdict, bool Counted, string? Flaw, string? Quote, bool? QuoteFound,
    double? Confidence, string? Error, string? Excerpt, decimal? CostUsd, IReadOnlyList<AiTraderDryRunAttempt> Attempts);

/// <summary>A model tried in a call, as its row's attempts show it: <c>ok</c>, or why it failed or was skipped.</summary>
public sealed record AiTraderDryRunAttempt(string Model, string Outcome, double Seconds);

/// <summary>
/// The Risk Memo on one plan. <c>AskedLive</c>: a live day asks it, the Validator having accepted. <c>Status</c> is the
/// report's would-be status (ok, invalid: not the JSON asked for, failed: no answer). <c>Proposed</c> is what the answer
/// asked for; <c>Applied</c> and <c>Ignored</c> what code would do with it and why; <c>Lots</c>, <c>StopLoss</c> and
/// <c>Target</c> the plan that would go on.
/// </summary>
public sealed record AiTraderDryRunMemo(
    bool AskedLive, long? CallId, string? Model, double Seconds, string Status, bool Parsed, string? Memo, string? Why, AiTraderDryRunProposed? Proposed,
    IReadOnlyList<string> Applied, IReadOnlyList<string> Ignored, int? Lots, decimal? StopLoss, decimal? Target, string? Error, string? Excerpt,
    decimal? CostUsd, IReadOnlyList<AiTraderDryRunAttempt> Attempts)
{
    public const string NotAskedStatus = "not-asked";

    public static AiTraderDryRunMemo NotAsked(bool askedLive, string why) =>
        new(askedLive, null, null, 0, NotAskedStatus, false, null, null, null, [], [], null, null, null, why, null, null, []);
}

/// <summary>The changes a memo's answer proposed, as numbers it wrote; null for one it left out.</summary>
public sealed record AiTraderDryRunProposed(double? Lots, double? StopLoss, double? Target);
