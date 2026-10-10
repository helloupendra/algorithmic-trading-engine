using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AlgoTrading.Api.Services.AiAgents;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// The Execution Reporter (owner, 10 Oct 2026): after each fill in the AI Trader's own manual book, entry or exit, code
/// compares the fill with the plan and the model adds a short note. One <c>ai_reports</c> row per fill (agent
/// <c>execution-reporter</c>, subject the paper order). The AI Trader's fills only: other users' books are not read.
/// </summary>
/// <remarks>
/// <para>
/// The numbers are code's (<see cref="Facts"/>): the fill against the price its decision saw (the ask for a buy, the bid
/// its own exit saw, the level for a stop or a target), the slippage in rupees and per cent, the time from the decision
/// to the fill, and the stop and target as placed. The model writes one or two sentences from them, and a note with a
/// number that is not among them is not kept (the report is then invalid, with code's numbers still in it).
/// </para>
/// <para>
/// One fill a scheduler minute, the oldest first, from the last <see cref="Window"/>. A fill no model answered for is
/// tried again like any failed report (<see cref="AiReportWriter.RetryAfter"/>, <see cref="AiSettings.MaxReportAttempts"/>).
/// </para>
/// </remarks>
public sealed class AiTraderExecutionReporter(
    TradingDbContext db,
    AiGateway gateway,
    AiReportWriter reports,
    AiSchedulerState schedule,
    ILotSizeResolver lotSizes,
    ILogger<AiTraderExecutionReporter> logger,
    TimeProvider? time = null) : IAiScheduledAgent
{
    /// <summary>How far back a fill is reported.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(2);

    public const string Clean = "clean";
    public const string Slipped = "slipped";
    public const string Slow = "slow";
    public const string Check = "check";

    private static readonly string[] Flags = [Clean, Slipped, Slow, Check];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public string AgentKey => AiCatalog.ExecutionReporter;

    public async Task<bool> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        long? id = await NextDueAsync(nowUtc, cancellationToken);
        if (id is null) return false;
        await ReportAsync(id.Value, cancellationToken);
        schedule.Worked(AgentKey, nowUtc);
        return true;
    }

    public async Task<AiReport?> RunForAsync(string? subjectId, CancellationToken cancellationToken)
    {
        long? id = long.TryParse(subjectId, NumberStyles.Integer, CultureInfo.InvariantCulture, out long given)
            ? given
            : await NextDueAsync(_time.GetUtcNow().UtcDateTime, cancellationToken);
        return id is null ? null : await ReportAsync(id.Value, cancellationToken);
    }

    /// <summary>The oldest AI Trader fill of the last <see cref="Window"/> with no settled report, or null.</summary>
    public async Task<long?> NextDueAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var books = await BooksAsync(cancellationToken);
        if (books.Count == 0) return null;
        var since = nowUtc - Window;
        var fills = await db.PaperOrders.AsNoTracking()
            .Where(o => books.Contains(o.SimulationRunId) && o.FillPrice != null && o.FilledUtc >= since)
            .OrderBy(o => o.FilledUtc).ThenBy(o => o.Id)
            .Select(o => o.Id)
            .Take(200)
            .ToListAsync(cancellationToken);
        if (fills.Count == 0) return null;

        var due = await reports.DueAsync(AgentKey, AiReportSubject.Order, fills.Select(Id).ToList(), cancellationToken);
        return fills.FirstOrDefault(f => due.Contains(Id(f))) is long next && next != 0 ? next : null;
    }

    /// <summary>Its manual books, every one it has had: an order elsewhere is not its own.</summary>
    private async Task<List<long>> BooksAsync(CancellationToken cancellationToken)
    {
        var account = await AiTraderAccount.FindAsync(db, cancellationToken);
        if (account is null) return [];
        return await db.SimulationRuns.AsNoTracking()
            .Where(r => r.UserId == account.Id && r.StrategyName == Controllers.ManualOrdersController.BookStrategyName)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<AiReport?> ReportAsync(long orderId, CancellationToken cancellationToken)
    {
        var books = await BooksAsync(cancellationToken);
        var order = await db.PaperOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId && books.Contains(o.SimulationRunId), cancellationToken);
        if (order?.FillPrice is null || order.FilledUtc is null) return null;

        var facts = await FactsAsync(order, cancellationToken);
        var day = IstTime.DateOf(order.FilledUtc.Value);
        string title = Title(facts);
        string factsJson = JsonSerializer.Serialize(facts, Json);

        AiAskResult result;
        try
        {
            result = await gateway.AskAsync(new AiAskInput(
                AgentKey, null, [new AiMessage("user", "The fill's facts, computed by code:\n" + factsJson + "\n\nWrite the note now: one JSON object.")],
                null, 1500, 0, $"execution-order-{orderId}", "schedule", AgentKey, null), NullAiStreamSink.Instance, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Execution Reporter: the ask on order {OrderId} failed", orderId);
            return await reports.SaveAsync(AgentKey, AiReportSubject.Order, Id(orderId), day, AiReportStatus.Failed, null, title,
                Body(facts, null), Data(facts, null, null), $"The ask failed: {ex.Message}", cancellationToken);
        }

        if (result.Outcome != AiCallOutcome.Ok)
        {
            string why = result.RefusalStatus is null ? result.Error : $"Refused: {result.Error}";
            logger.LogInformation("Execution Reporter on order {OrderId}: no note ({Error})", orderId, why);
            return AiGateway.IsCapacityRefusal(result)
                ? await reports.SaveTurnedAwayAsync(AgentKey, AiReportSubject.Order, Id(orderId), day, result, title, why, cancellationToken)
                : await reports.SaveAsync(AgentKey, AiReportSubject.Order, Id(orderId), day, AiReportStatus.Failed, result, title,
                    Body(facts, null), Data(facts, null, null), why, cancellationToken);
        }

        var (note, flag, error) = ReadNote(result.Text, factsJson);
        return await reports.SaveAsync(AgentKey, AiReportSubject.Order, Id(orderId), day, error is null ? AiReportStatus.Ok : AiReportStatus.Invalid,
            result, title, Body(facts, error is null ? note : null), Data(facts, note, flag), error ?? string.Empty, cancellationToken);
    }

    /// <summary>
    /// The note read from the model's answer, or why it is not kept: not the JSON asked for, a flag that is not one of
    /// clean, slipped, slow or check, no note, or a number in the note that is not among the facts.
    /// </summary>
    public static (string? Note, string? Flag, string? Error) ReadNote(string answer, string factsJson)
    {
        var obj = AiJson.Object(answer);
        if (obj is null) return (null, null, "The answer was not the JSON object asked for.");
        string? flag = AiJson.Str(obj, "flag")?.ToLowerInvariant();
        string? note = AiJson.Str(obj, "note");
        if (flag is null || !Flags.Contains(flag)) return (note, flag, $"The flag was \"{flag}\", not clean, slipped, slow or check.");
        if (note is null) return (null, flag, "No note.");
        if (note.Length > 600) return (note, flag, "The note is longer than two sentences.");
        if (UnknownNumber(note, factsJson) is string n) return (note, flag, $"The note says {n}, which is not among the facts.");
        return (note, flag, null);
    }

    private static readonly Regex Number = new(@"\d[\d,]*(?:\.\d+)?", RegexOptions.CultureInvariant);

    /// <summary>The first number in <paramref name="note"/> that is not in the facts, as given or rounded; null when every one is.</summary>
    public static string? UnknownNumber(string note, string factsJson)
    {
        var known = new HashSet<decimal>();
        foreach (Match m in Number.Matches(factsJson))
        {
            if (!decimal.TryParse(m.Value.Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v)) continue;
            known.Add(v);
            for (int d = 0; d <= 2; d++)
            {
                known.Add(Math.Round(v, d, MidpointRounding.AwayFromZero));
                known.Add(Math.Round(v, d, MidpointRounding.ToEven));
            }
        }

        foreach (Match m in Number.Matches(note))
        {
            if (!decimal.TryParse(m.Value.Replace(",", string.Empty).TrimEnd('.'), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal v)) continue;
            if (!known.Contains(v)) return m.Value;
        }

        return null;
    }

    /// <summary>
    /// What code knows of one fill: the order, how it was priced, the position it opened or closed, the decision behind
    /// it, and the comparisons the note is written from.
    /// </summary>
    public async Task<ExecutionFacts> FactsAsync(PaperOrder order, CancellationToken cancellationToken)
    {
        int lotSize = (await lotSizes.ResolveAsync(order.Symbol, cancellationToken)).LotSize;
        int units = order.Quantity * Math.Max(1, lotSize);
        bool entry = order.Side == "BUY";
        decimal fill = order.FillPrice!.Value;
        var pricing = PaperFill.ReadSource(order.MetadataJson);

        var position = await db.PaperPositions.AsNoTracking()
            .Where(p => p.SimulationRunId == order.SimulationRunId && p.GroupId == order.GroupId && p.Symbol == order.Symbol)
            .OrderByDescending(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var signal = order.SimulationSignalId is long sid
            ? await db.SimulationSignals.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, cancellationToken)
            : null;
        var meta = AiJson.Object(signal?.MetadataJson);

        // The decisions of the fill's day that placed something: an entry is named by its order or group, its own exit by its order.
        var day = IstTime.DateOf(order.FilledUtc!.Value);
        var placed = await db.AiTraderDecisions.AsNoTracking()
            .Where(d => d.Mode == AiTraderModes.Live && d.Executed && d.Day >= day.AddDays(-5) && d.Day <= day)
            .ToListAsync(cancellationToken);
        AiTraderDecision? entryDecision = placed.FirstOrDefault(d => Under(d.ResultJson, "placed", "groupId") == order.GroupId);
        AiTraderDecision? exitDecision = entry ? null : placed.FirstOrDefault(d => Under(d.ResultJson, "exit", "orderId") == Id(order.Id));

        string trigger;
        decimal? reference;
        string? referenceIs;
        DateTime? decidedUtc;
        if (entry)
        {
            trigger = "entry";
            reference = Dec(entryDecision?.ResultJson, "contract", "ask") ?? Dec(entryDecision?.ResultJson, "placed", "ask");
            referenceIs = "the ask its decision saw";
            decidedUtc = entryDecision?.ClockUtc;
        }
        else
        {
            string by = meta is null ? string.Empty : AiJson.Str(meta, "by") ?? string.Empty;
            string reason = meta is null ? string.Empty : AiJson.Str(meta, "reason") ?? string.Empty;
            if (exitDecision is not null || by == AiTraderAccount.UserName)
            {
                trigger = "its exit";
                reference = Dec(exitDecision?.ResultJson, "exit", "bidSeen");
                referenceIs = "the bid seen just before its exit";
                decidedUtc = exitDecision?.ClockUtc;
            }
            else if (by == StrategyRiskGuardService.By && reason.StartsWith("Stop-loss", StringComparison.Ordinal))
            {
                trigger = "stop";
                reference = position?.StopLossPrice;
                referenceIs = "its stop";
                decidedUtc = null;
            }
            else if (by == StrategyRiskGuardService.By && reason.StartsWith("Target", StringComparison.Ordinal))
            {
                trigger = "target";
                reference = position?.TargetPrice;
                referenceIs = "its target";
                decidedUtc = null;
            }
            else
            {
                trigger = by == StrategyRunControl.MarketCloseBy || reason.StartsWith("Intraday", StringComparison.Ordinal) ? "close" : by.Length > 0 ? by : "closed";
                reference = null;
                referenceIs = null;
                decidedUtc = null;
            }
        }

        // Positive is worse: paid over the reference on a buy, sold under it on a sell.
        decimal? perUnit = reference is decimal r ? Math.Round(entry ? fill - r : r - fill, 2) : null;
        decimal? rupees = perUnit is decimal u ? Math.Round(u * units, 2) : null;
        decimal? percent = perUnit is decimal p && reference is decimal rf && rf != 0 ? Math.Round(p / rf * 100m, 2) : null;

        decimal? entryPrice = position?.AveragePrice;
        decimal? stop = position?.StopLossPrice;
        decimal? target = position?.TargetPrice;
        return new ExecutionFacts(
            order.Id, order.SimulationRunId, order.Symbol, order.Side, entry ? "entry" : "exit", trigger, order.Quantity, lotSize, units, fill,
            Ist(order.FilledUtc.Value), pricing?.Note, pricing?.QuoteAgeSeconds, pricing?.StaleQuote == true,
            reference, referenceIs, perUnit, rupees, percent,
            entryDecision?.Id, exitDecision?.Id, decidedUtc is DateTime at ? Ist(at) : null,
            decidedUtc is DateTime from ? (int)Math.Round((order.FilledUtc.Value - from).TotalSeconds) : null,
            position?.Id, entryPrice, stop, target,
            entryPrice is decimal e && stop is decimal s ? Math.Round(e - s, 2) : null,
            entryPrice is decimal e2 && stop is decimal s2 && e2 != 0 ? Math.Round((e2 - s2) / e2 * 100m, 2) : null,
            entryPrice is decimal e3 && target is decimal t ? Math.Round(t - e3, 2) : null,
            entryPrice is decimal e4 && target is decimal t2 && e4 != 0 ? Math.Round((t2 - e4) / e4 * 100m, 2) : null,
            entry || position is null ? null : Math.Round(position.RealizedPnl, 2));
    }

    private static string? Under(string? json, string key, string field)
    {
        try
        {
            return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json)?[key]?[field] is JsonValue v
                ? v.TryGetValue(out string? s) ? s : v.TryGetValue(out long l) ? Id(l) : null
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal? Dec(string? json, string key, string field)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json)?[key]?[field] is JsonValue v && v.TryGetValue(out decimal d) ? d : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"Order #123 · BUY NIFTY26O0622650CE ×1 lot at ₹120.50: ₹0.50 (0.42%) over the ask its decision saw, 41 s after its look".</summary>
    public static string Title(ExecutionFacts f)
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"Order #{f.OrderId} · {f.Side} {f.Symbol} ×{f.Lots} lot{(f.Lots == 1 ? "" : "s")} at ₹{f.FillPrice:0.00}"));
        if (f.SlippagePerUnit is decimal u && f.ReferenceIs is not null)
        {
            text.Append(": ")
                .Append(u == 0 ? "at " : string.Create(CultureInfo.InvariantCulture, $"₹{Math.Abs(u):0.00} {(u > 0 ? "worse" : "better")} than "))
                .Append(f.ReferenceIs);
            if (f.SlippagePercent is decimal p && u != 0) text.Append(CultureInfo.InvariantCulture, $" ({Math.Abs(p):0.##}%)");
        }
        else if (f.Trigger == "close")
        {
            text.Append(": squared off at the close");
        }

        if (f.SecondsToFill is int s) text.Append(CultureInfo.InvariantCulture, $", {s} s after its look");
        return text.ToString();
    }

    private static string Body(ExecutionFacts f, string? note)
    {
        var body = new StringBuilder();
        if (note is not null) body.Append(note).Append("\n\n");
        body.Append(CultureInfo.InvariantCulture, $"**Fill.** {f.Side} {f.Lots} lot(s) × {f.LotSize} of {f.Symbol} at ₹{f.FillPrice:0.00}, {f.FilledIst} IST ({f.Kind}, {f.Trigger}).");
        if (f.Pricing is not null) body.Append(' ').Append(f.Pricing).Append('.');
        body.Append('\n');
        if (f.Reference is decimal r)
        {
            body.Append(CultureInfo.InvariantCulture, $"**Against the plan.** {f.ReferenceIs}: ₹{r:0.00}; slippage ₹{f.SlippagePerUnit:0.00} a unit, ₹{f.SlippageRupees:0.00} in all ({f.SlippagePercent:0.##}%). Positive is worse.\n");
        }

        if (f.SecondsToFill is int s) body.Append(CultureInfo.InvariantCulture, $"**Time.** Decided at {f.DecidedIst} IST, filled {s} s later.\n");
        if (f.Entry is decimal e)
        {
            body.Append(CultureInfo.InvariantCulture, $"**As placed.** Entry ₹{e:0.00}");
            if (f.Stop is decimal st) body.Append(CultureInfo.InvariantCulture, $", stop ₹{st:0.00} (₹{f.StopDistance:0.00}, {f.StopDistancePercent:0.##}% under)");
            if (f.Target is decimal t) body.Append(CultureInfo.InvariantCulture, $", target ₹{t:0.00} (₹{f.TargetDistance:0.00}, {f.TargetDistancePercent:0.##}% over)");
            body.Append(".\n");
        }

        if (f.RealizedPnl is decimal pnl) body.Append(CultureInfo.InvariantCulture, $"**Realized.** ₹{pnl:0.00} before charges.\n");
        return body.ToString();
    }

    private static string Data(ExecutionFacts facts, string? note, string? flag)
    {
        var data = JsonSerializer.SerializeToNode(facts, Json)!.AsObject();
        if (note is not null) data["note"] = note;
        if (flag is not null) data["flag"] = flag;
        return data.ToJsonString(Json);
    }

    private static string Ist(DateTime utc) => IstTime.ToIst(utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// One AI Trader fill as code compares it with its plan. <c>Slippage*</c> is against <c>Reference</c> (null when the
/// fill had none to answer to, a square-off at the close); positive is worse. <c>SecondsToFill</c> is from its
/// decision's look to the fill, for a fill a decision made.
/// </summary>
public sealed record ExecutionFacts(
    long OrderId, long BookRunId, string Symbol, string Side, string Kind, string Trigger, int Lots, int LotSize, int Units, decimal FillPrice,
    string FilledIst, string? Pricing, double? QuoteAgeSeconds, bool StaleQuote,
    decimal? Reference, string? ReferenceIs, decimal? SlippagePerUnit, decimal? SlippageRupees, decimal? SlippagePercent,
    long? EntryDecisionId, long? ExitDecisionId, string? DecidedIst, int? SecondsToFill,
    long? PositionId, decimal? Entry, decimal? Stop, decimal? Target, decimal? StopDistance, decimal? StopDistancePercent,
    decimal? TargetDistance, decimal? TargetDistancePercent, decimal? RealizedPnl);
