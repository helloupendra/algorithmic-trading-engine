using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// The Validator / Skeptic (owner, 10 Oct 2026): before the AI Trader places a buy or starts a strategy that the rules
/// allowed, the Judge is asked three times, sampled, for ACCEPT or REJECT with the plan's biggest flaw. Two REJECTs
/// block the placement, with the flaw named. Two ACCEPTs let it through. Anything else, a provider down, the agent
/// switched off, answers that cannot be read, blocks it too: the desk fails closed, and says so.
/// </summary>
/// <remarks>
/// <para>
/// A REJECT must quote the words of the brief or the plan that show its flaw, and the quote is checked by code: a
/// REJECT resting on words it was never given is not counted, as a News Analyst record with a number not in its item
/// is invalid. An ACCEPT needs no quote.
/// </para>
/// <para>
/// It is asked outside <see cref="AiTraderAgent.BookGate"/>: three Judge answers can take minutes, and the shadow book's
/// minute check and another look must not wait on them. The plan is judged again on the fresh book after it.
/// </para>
/// <para>
/// Each decision it judges is one <c>ai_reports</c> row (agent <c>validator</c>, subject the decision), with every
/// sample's verdict, flaw, quote and call.
/// </para>
/// </remarks>
public sealed class AiTraderValidator(AiGateway gateway, AiReportWriter reports, ILogger<AiTraderValidator> logger)
{
    /// <summary>Asks per plan.</summary>
    public const int Samples = 3;

    /// <summary>Answers of one kind that decide: two of three.</summary>
    public const int Majority = 2;

    /// <summary>Sampled, so three asks are three looks rather than one answer three times.</summary>
    public const double Temperature = 0.7;

    /// <summary>The rule a block records on the decision: a majority rejected it.</summary>
    public const string RejectedRule = "validator";

    /// <summary>The rule a block records when no majority answered: the desk fails closed.</summary>
    public const string UnavailableRule = "validator-unavailable";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<AiTraderValidation> JudgeAsync(AiTraderDecision decision, AiTraderPlan plan, AiTraderContract? contract, AiTraderVerdict verdict,
        CancellationToken cancellationToken)
    {
        string facts = Facts(plan, contract, verdict);
        string question = facts + "\nMARKET BRIEF THE PLAN RESTS ON\n" + decision.Brief + "\n\nJudge the plan now: one JSON object.";
        string source = facts + "\n" + decision.Brief;

        var samples = new List<AiTraderValidatorSample>();
        AiAskResult? last = null;
        for (int n = 1; n <= Samples; n++)
        {
            AiAskResult result;
            try
            {
                result = await gateway.AskAsync(new AiAskInput(
                    AiCatalog.Validator, null, [new AiMessage("user", question)], null, 3000, Temperature,
                    $"validator-decision-{decision.Id}-{n}", "schedule", AiCatalog.Validator, null), NullAiStreamSink.Instance, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Validator: ask {N} on decision {DecisionId} failed", n, decision.Id);
                samples.Add(AiTraderValidatorSample.NoAnswer(null, string.Empty, $"The ask failed: {ex.Message}"));
                continue;
            }

            last = result;
            samples.Add(Read(result, source));
            // Switched off or no key: the next asks would be refused the same way.
            if (result.Outcome == AiCallOutcome.Refused && result.RefusalStatus is 409 or 503) break;
        }

        var validation = Tally(samples);
        try
        {
            var report = await reports.SaveAsync(AiCatalog.Validator, AiReportSubject.Decision, decision.Id.ToString(CultureInfo.InvariantCulture), decision.Day,
                validation.Outcome == AiTraderValidation.Unavailable ? AiReportStatus.Failed : AiReportStatus.Ok, last,
                Title(decision.Id, validation), Body(plan, contract, validation), JsonSerializer.Serialize(validation.Data(), Json),
                validation.Outcome == AiTraderValidation.Unavailable ? validation.Why : string.Empty, CancellationToken.None);
            validation = validation with { ReportId = report.Id };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The verdict stands without its report; the decision keeps it.
            logger.LogWarning(ex, "Validator: its report on decision {DecisionId} could not be saved", decision.Id);
        }

        logger.LogInformation("Validator on AI Trader decision {DecisionId}: {Outcome} ({Accepts} accept, {Rejects} reject, {Bad} no answer)",
            decision.Id, validation.Outcome, validation.Accepts, validation.Rejects, validation.NoAnswers);
        return validation;
    }

    /// <summary>What the validator is told besides the brief: the plan, the contract and its price, the rules' verdict.</summary>
    public static string Facts(AiTraderPlan plan, AiTraderContract? contract, AiTraderVerdict verdict)
    {
        var text = new StringBuilder("THE PLAN (as the AI Trader proposed it)\n");
        text.Append(JsonSerializer.Serialize(plan, Json)).Append('\n');
        if (contract is not null)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"THE CONTRACT: {contract.Symbol}, {contract.Underlying} {contract.Strike:0.##} {contract.OptionType} expiring {contract.Expiry:yyyy-MM-dd}, ")
                .Append(CultureInfo.InvariantCulture, $"bought at about {contract.Ask:0.##} (the ask), lot size {contract.LotSize}")
                .Append(plan.Lots is int lots ? string.Create(CultureInfo.InvariantCulture, $", {lots} lot(s) = {AiTraderGuard.Rupees(contract.Ask * lots * contract.LotSize)} of premium") : string.Empty)
                .Append('\n');
        }

        text.Append("THE RULES' VERDICT: ").Append(verdict.Allowed ? "allowed: " : "refused: ").Append(verdict.Why).Append('\n');
        return text.ToString();
    }

    /// <summary>One answer as a sample: its verdict, flaw and quote, or why it does not count.</summary>
    public static AiTraderValidatorSample Read(AiAskResult result, string source)
    {
        if (result.Outcome != AiCallOutcome.Ok)
        {
            string why = result.RefusalStatus is null ? $"No model answered: {result.Error}" : $"Refused: {result.Error}";
            return AiTraderValidatorSample.NoAnswer(result.CallId, result.Model, why);
        }

        var obj = AiJson.Object(result.Text);
        if (obj is null) return AiTraderValidatorSample.NoAnswer(result.CallId, result.Model, "The answer was not the JSON object asked for.");

        string? said = AiJson.Str(obj, "verdict")?.ToUpperInvariant();
        string? flaw = AiJson.Str(obj, "flaw");
        string? quote = AiJson.Str(obj, "quote");
        double? confidence = AiJson.Num(obj, "confidence") is double c && c is >= 0 and <= 1 ? c : null;
        if (said is not ("ACCEPT" or "REJECT"))
        {
            return AiTraderValidatorSample.NoAnswer(result.CallId, result.Model, $"The verdict was \"{said}\", not ACCEPT or REJECT.");
        }

        if (flaw is { Length: > MaxFlaw }) flaw = flaw[..(MaxFlaw - 1)] + "…";
        if (said == "REJECT")
        {
            if (flaw is null) return AiTraderValidatorSample.NoAnswer(result.CallId, result.Model, "A REJECT that names no flaw.");
            if (quote is null || !Quotes(source, quote))
            {
                return AiTraderValidatorSample.NoAnswer(result.CallId, result.Model,
                    $"A REJECT whose quote is not in the brief or the plan: \"{Cut(quote ?? string.Empty, 160)}\".") with { Flaw = flaw, Quote = quote };
            }
        }

        return new AiTraderValidatorSample(said, flaw, quote, confidence, result.CallId, result.Model, null);
    }

    private const int MaxFlaw = 300;

    /// <summary>Whether <paramref name="quote"/> is in <paramref name="source"/>, ignoring case, spacing and the quote marks around it.</summary>
    public static bool Quotes(string source, string quote)
    {
        string q = Normal(quote.Trim().Trim('"', '“', '”', '\'', '‘', '’').Trim());
        return q.Length >= 4 && Normal(source).Contains(q, StringComparison.Ordinal);
    }

    private static string Normal(string text) => Regex.Replace(text.ToLowerInvariant(), @"\s+", " ");

    /// <summary>Two of a kind decide; anything else is no verdict, and the plan is not placed.</summary>
    public static AiTraderValidation Tally(IReadOnlyList<AiTraderValidatorSample> samples)
    {
        int accepts = samples.Count(s => s.Verdict == "ACCEPT");
        var rejects = samples.Where(s => s.Verdict == "REJECT").ToList();
        int bad = samples.Count(s => s.Verdict is null);
        if (rejects.Count >= Majority)
        {
            var named = rejects.OrderByDescending(s => s.Confidence ?? 0).First();
            return new AiTraderValidation(AiTraderValidation.Rejected, accepts, rejects.Count, bad, named.Flaw, named.Quote,
                $"The Validator rejected the plan ({rejects.Count} of {samples.Count}): {named.Flaw}", samples);
        }

        if (accepts >= Majority)
        {
            return new AiTraderValidation(AiTraderValidation.Accepted, accepts, rejects.Count, bad, null, null,
                $"The Validator accepted the plan ({accepts} of {samples.Count}).", samples);
        }

        string errors = string.Join("; ", samples.Where(s => s.Error is not null).Select(s => s.Error).Distinct().Take(2));
        return new AiTraderValidation(AiTraderValidation.Unavailable, accepts, rejects.Count, bad, null, null,
            $"The Validator gave no verdict ({accepts} accept, {rejects.Count} reject, {bad} no answer of {Samples} asks), so nothing was placed: " +
            "a plan it has not accepted is never placed." + (errors.Length > 0 ? $" {errors}" : string.Empty),
            samples);
    }

    private static string Title(long decisionId, AiTraderValidation v) => v.Outcome switch
    {
        AiTraderValidation.Accepted => $"Decision #{decisionId}: ACCEPT ({v.Accepts} of {v.Samples.Count})",
        AiTraderValidation.Rejected => Cut($"Decision #{decisionId}: REJECT ({v.Rejects} of {v.Samples.Count}): {v.Flaw}", 300),
        _ => $"Decision #{decisionId}: no verdict ({v.Accepts} accept, {v.Rejects} reject, {v.NoAnswers} no answer): not placed",
    };

    private static string Body(AiTraderPlan plan, AiTraderContract? contract, AiTraderValidation v)
    {
        var body = new StringBuilder();
        body.Append("**Plan.** ").Append(plan.Action).Append(' ').Append(plan.Underlying);
        if (contract is not null) body.Append(CultureInfo.InvariantCulture, $" {contract.Strike:0.##} {contract.OptionType} at about {contract.Ask:0.##}");
        if (plan.Lots is int lots) body.Append(CultureInfo.InvariantCulture, $", {lots} lot(s)");
        if (plan.StopLoss is decimal stop) body.Append(CultureInfo.InvariantCulture, $", stop {stop:0.##}");
        if (plan.Target is decimal target) body.Append(CultureInfo.InvariantCulture, $", target {target:0.##}");
        body.Append(". _").Append(plan.Reason).Append("_\n\n");
        body.Append("**Verdict.** ").Append(v.Why).Append("\n\n");
        int n = 0;
        foreach (var s in v.Samples)
        {
            n++;
            body.Append(CultureInfo.InvariantCulture, $"{n}. ");
            if (s.Verdict is null) body.Append("No verdict: ").Append(s.Error);
            else
            {
                body.Append(s.Verdict);
                if (s.Flaw is not null) body.Append(": ").Append(s.Flaw);
                if (s.Quote is not null) body.Append(" (\"").Append(Cut(s.Quote, 200)).Append("\")");
                if (s.Confidence is double c) body.Append(CultureInfo.InvariantCulture, $", confidence {c:0.00}");
            }

            body.Append('\n');
        }

        return body.ToString();
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>One of the Validator's asks: its verdict (null when it does not count), flaw, quote and call.</summary>
public sealed record AiTraderValidatorSample(string? Verdict, string? Flaw, string? Quote, double? Confidence, long? CallId, string Model, string? Error)
{
    public static AiTraderValidatorSample NoAnswer(long? callId, string model, string error) => new(null, null, null, null, callId, model, error);
}

/// <summary>
/// The Validator's verdict on one plan. <c>Outcome</c> is accepted, rejected or unavailable; only accepted is placed.
/// <c>Why</c> is what the decision records.
/// </summary>
public sealed record AiTraderValidation(string Outcome, int Accepts, int Rejects, int NoAnswers, string? Flaw, string? Quote, string Why,
    IReadOnlyList<AiTraderValidatorSample> Samples, long? ReportId = null)
{
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Unavailable = "unavailable";

    public bool Placeable => Outcome == Accepted;

    /// <summary>The rule a block records on the decision.</summary>
    public string Rule => Outcome == Rejected ? AiTraderValidator.RejectedRule : AiTraderValidator.UnavailableRule;

    /// <summary>What the decision's result and the report's data keep.</summary>
    public object Data() => new
    {
        reportId = ReportId,
        outcome = Outcome,
        accepts = Accepts,
        rejects = Rejects,
        noAnswers = NoAnswers,
        flaw = Flaw,
        quote = Quote,
        samples = Samples.Select(s => new { verdict = s.Verdict, flaw = s.Flaw, quote = s.Quote, confidence = s.Confidence, callId = s.CallId, model = s.Model, error = s.Error }),
    };
}

/// <summary>
/// The Risk Memo (owner, 10 Oct 2026): beside the rules' verdict on a plan the Validator accepted, a short memo on what
/// it risks against the account's exposure, from the Judge; it may tighten the plan (fewer lots, a higher stop, a lower
/// target). Code applies only a change that is tighter and still valid, and records anything it ignored and why.
/// </summary>
/// <remarks>
/// It never blocks: the veto is the rules' and the Validator's. A memo that cannot be had (no answer, an answer that is
/// not the JSON asked for) leaves the plan as it was, and says so. Each decision it reads is one <c>ai_reports</c> row
/// (agent <c>risk-memo</c>, subject the decision). Asked outside <see cref="AiTraderAgent.BookGate"/>, like the Validator.
/// </remarks>
public sealed class AiTraderRiskMemo(AiGateway gateway, AiReportWriter reports, ILogger<AiTraderRiskMemo> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<AiTraderMemo> ReviewAsync(AiTraderDecision decision, AiTraderPlan plan, AiTraderContract? contract, AiTraderVerdict verdict,
        AiTraderBook book, AiTraderRules rules, CancellationToken cancellationToken)
    {
        string question = Facts(plan, contract, verdict, book, rules) + "\nWrite the memo now: one JSON object.";
        AiAskResult? result = null;
        AiTraderMemo memo;
        try
        {
            result = await gateway.AskAsync(new AiAskInput(
                AiCatalog.RiskMemo, null, [new AiMessage("user", question)], null, 3000, 0.2,
                $"risk-memo-decision-{decision.Id}", "schedule", AiCatalog.RiskMemo, null), NullAiStreamSink.Instance, cancellationToken);
            memo = result.Outcome == AiCallOutcome.Ok
                ? Apply(plan, contract, result.Text)
                : AiTraderMemo.Unchanged(plan, AiReportStatus.Failed,
                    result.RefusalStatus is null ? $"No model answered: {result.Error}" : $"Refused: {result.Error}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Risk memo on decision {DecisionId} failed", decision.Id);
            memo = AiTraderMemo.Unchanged(plan, AiReportStatus.Failed, $"The ask failed: {ex.Message}");
        }

        try
        {
            var report = await reports.SaveAsync(AiCatalog.RiskMemo, AiReportSubject.Decision, decision.Id.ToString(CultureInfo.InvariantCulture), decision.Day,
                memo.Status, result, Title(decision.Id, memo), Body(memo), JsonSerializer.Serialize(memo.Data(), Json),
                memo.Status == AiReportStatus.Ok ? string.Empty : memo.Error ?? string.Empty, CancellationToken.None);
            memo = memo with { ReportId = report.Id };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Risk memo: its report on decision {DecisionId} could not be saved", decision.Id);
        }

        return memo;
    }

    /// <summary>The facts the memo reads: the plan, the contract and its price, the rules' verdict, the limits and the account's exposure.</summary>
    public static string Facts(AiTraderPlan plan, AiTraderContract? contract, AiTraderVerdict verdict, AiTraderBook book, AiTraderRules rules)
    {
        var text = new StringBuilder(AiTraderValidator.Facts(plan, contract, verdict));
        if (contract is not null && plan.Lots is int lots && plan.StopLoss is decimal stop)
        {
            decimal atStop = (contract.Ask - stop) * lots * contract.LotSize;
            text.Append(CultureInfo.InvariantCulture, $"AT ITS STOP it loses about {AiTraderGuard.Rupees(Math.Round(atStop, 0))} before charges.\n");
        }

        decimal budget = Math.Max(0m, rules.DailyLossLimit + Math.Min(0m, book.NetToday));
        text.Append("THE OWNER'S LIMITS: ")
            .Append(CultureInfo.InvariantCulture, $"1 to {rules.MaxLotsPerTrade} lots; at most {AiTraderGuard.Rupees(rules.MaxPremiumPerTrade)} of premium a trade; ")
            .Append(CultureInfo.InvariantCulture, $"at most {rules.MaxOpenPositions} open positions and {AiTraderGuard.Rupees(rules.Capital)} of premium in use; ")
            .Append(CultureInfo.InvariantCulture, $"a stop no more than {rules.MaxStopFraction * 100:0}% under the entry; nothing new at a net of −{AiTraderGuard.Rupees(rules.DailyLossLimit)} for the day.\n");
        text.Append("THE ACCOUNT NOW: ")
            .Append(CultureInfo.InvariantCulture, $"net today {AiTraderGuard.Rupees(book.NetToday)} after charges; loss budget left {AiTraderGuard.Rupees(budget)}; ")
            .Append(CultureInfo.InvariantCulture, $"trades opened today {book.OpenedToday} of {rules.MaxTradesPerDay}; premium in use {AiTraderGuard.Rupees(book.Open.Sum(p => p.PremiumInUse))}.\n");
        foreach (var p in book.Open)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"- open: {p.Symbol} × {p.Lots} lot(s), entry {p.Entry:0.##}, P&L {AiTraderGuard.Rupees(p.UnrealizedPnl)}, stop {(p.StopLoss?.ToString("0.##", CultureInfo.InvariantCulture) ?? "none")}\n");
        }

        foreach (var r in book.Runs)
        {
            text.Append(CultureInfo.InvariantCulture, $"- running: run {r.RunId} {r.Strategy} on {r.Underlying}, net {AiTraderGuard.Rupees(r.NetPnl)}\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// The memo read from the model's answer, with only its tightenings applied: fewer lots (at least 1); a higher stop
    /// that is still under the price it is bought at; a lower target that is still above it. A strategy start takes
    /// fewer lots only. Anything else is ignored, and each ignored change says why.
    /// </summary>
    public static AiTraderMemo Apply(AiTraderPlan plan, AiTraderContract? contract, string answer)
    {
        var obj = AiJson.Object(answer);
        string? text = obj is null ? null : AiJson.Str(obj, "memo");
        if (obj is null || text is null)
        {
            return AiTraderMemo.Unchanged(plan, AiReportStatus.Invalid, "The answer was not the JSON object asked for, with a memo.") with
            {
                Text = Cut(answer, 2000),
            };
        }

        var applied = new List<string>();
        var ignored = new List<string>();
        var result = plan;
        decimal? price = contract?.Ask;

        if (obj["lots"] is not null && AiJson.Num(obj, "lots") is double l)
        {
            int current = plan.Lots ?? 1;
            int lots = (int)l;
            if (l != Math.Floor(l)) ignored.Add($"lots {l.ToString(CultureInfo.InvariantCulture)} is not a whole number of lots");
            else if (lots > current) ignored.Add($"lots {lots} is more than the plan's {current}: looser, ignored");
            else if (lots < 1) ignored.Add($"lots {lots} is under 1");
            else if (lots < current)
            {
                result = result with { Lots = lots };
                applied.Add($"lots {current} → {lots}");
            }
        }

        bool buy = plan.Action == AiTraderPlan.Buy;
        if (obj["stopLoss"] is not null && AiJson.Num(obj, "stopLoss") is double sd)
        {
            decimal stop = Math.Round((decimal)sd, 2);
            if (!buy) ignored.Add("a strategy start has no stop to tighten");
            else if (plan.StopLoss is not decimal current) ignored.Add("the plan has no stop to tighten");
            else if (stop < current) ignored.Add(Say($"stop {stop:0.##} is under the plan's {current:0.##}: looser, ignored"));
            else if (price is decimal ask && stop >= ask) ignored.Add(Say($"stop {stop:0.##} is at or above the price it is bought at ({ask:0.##})"));
            else if (stop > current)
            {
                result = result with { StopLoss = stop };
                applied.Add(Say($"stop {current:0.##} → {stop:0.##}"));
            }
        }

        if (obj["target"] is not null && AiJson.Num(obj, "target") is double td)
        {
            decimal target = Math.Round((decimal)td, 2);
            if (!buy) ignored.Add("a strategy start has no target to tighten");
            else if (plan.Target is not decimal current) ignored.Add("the plan has no target to tighten");
            else if (target > current) ignored.Add(Say($"target {target:0.##} is over the plan's {current:0.##}: looser, ignored"));
            else if (price is decimal ask && target <= ask) ignored.Add(Say($"target {target:0.##} is at or under the price it is bought at ({ask:0.##})"));
            else if (target < current)
            {
                result = result with { Target = target };
                applied.Add(Say($"target {current:0.##} → {target:0.##}"));
            }
        }

        return new AiTraderMemo(AiReportStatus.Ok, Cut(text, 1500), AiJson.Str(obj, "why"), result, applied, ignored, null);
    }

    /// <summary>A change in words, its numbers written the same on any server.</summary>
    private static string Say(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Title(long decisionId, AiTraderMemo m) => m.Status switch
    {
        AiReportStatus.Ok when m.Applied.Count > 0 => Cut($"Decision #{decisionId}: tightened ({string.Join(", ", m.Applied)})", 300),
        AiReportStatus.Ok => $"Decision #{decisionId}: memo, plan unchanged",
        _ => $"Decision #{decisionId}: no memo, plan unchanged",
    };

    private static string Body(AiTraderMemo m)
    {
        var body = new StringBuilder();
        if (m.Status == AiReportStatus.Ok) body.Append(m.Text).Append("\n\n");
        else body.Append("No memo: ").Append(m.Error).Append(". The plan went on unchanged: the memo can only tighten, never block.\n\n");
        if (m.Applied.Count > 0) body.Append("**Applied.** ").Append(string.Join("; ", m.Applied)).Append(m.Why is null ? "." : $". {m.Why}").Append('\n');
        if (m.Ignored.Count > 0) body.Append("**Ignored.** ").Append(string.Join("; ", m.Ignored)).Append(".\n");
        return body.ToString();
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

/// <summary>
/// A risk memo and the plan as it stands after it: <c>Plan</c> carries only the tightenings applied. <c>Status</c> is the
/// report's (ok, invalid, failed); a memo that is not ok changed nothing.
/// </summary>
public sealed record AiTraderMemo(string Status, string Text, string? Why, AiTraderPlan Plan, IReadOnlyList<string> Applied, IReadOnlyList<string> Ignored,
    string? Error, long? ReportId = null)
{
    public bool Tightened => Applied.Count > 0;

    public static AiTraderMemo Unchanged(AiTraderPlan plan, string status, string error) => new(status, string.Empty, null, plan, [], [], error);

    /// <summary>What the decision's result and the report's data keep.</summary>
    public object Data() => new
    {
        reportId = ReportId,
        status = Status,
        memo = Text.Length == 0 ? null : Text,
        why = Why,
        tightened = Tightened,
        applied = Applied,
        ignored = Ignored,
        lots = Plan.Lots,
        stopLoss = Plan.StopLoss,
        target = Plan.Target,
        error = Error,
    };
}
