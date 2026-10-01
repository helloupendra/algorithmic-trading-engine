using System.Text.Json;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The AI Trader's record (<see cref="AiTraderAgent"/>): whether it is on and placing, its limits, and every
/// decision with the brief it read. Admin only. It is switched on and off, and run once by hand, on AI → Agents.
/// </summary>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/[controller]")]
public class AiTraderController(TradingDbContext db, AiSettingsStore store, IOptionsMonitor<AiSettings> settings, TimeProvider? time = null) : ControllerBase
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken cancellationToken)
    {
        var state = await store.LoadAsync(cancellationToken);
        var s = settings.CurrentValue;
        var today = IstTime.DateOf(_time.GetUtcNow().UtcDateTime);
        var rows = await db.AiTraderDecisions.AsNoTracking()
            .Where(d => d.Day == today && d.ReplaySessionId == null)
            .Select(d => new { d.Action, d.Allowed, d.Rule })
            .ToListAsync(cancellationToken);
        var latest = await Summaries(db.AiTraderDecisions.AsNoTracking().Where(d => d.ReplaySessionId == null), 3, cancellationToken);
        var shadow = await db.AiTraderShadowPositions.AsNoTracking()
            .Where(p => p.ReplaySessionId == null && p.Day == today).ToListAsync(cancellationToken);

        return Ok(new AiTraderStatus(
            state.Agent(AiCatalog.AiTrader)?.Status ?? "off",
            s.AiTraderExecute ? AiTraderModes.Live : AiTraderModes.Shadow,
            s.AiTraderEveryMinutes,
            AiTraderAgent.Rules,
            new AiTraderDay(today.ToString("yyyy-MM-dd"), rows.Count, rows.Count(r => r.Action is not ("" or AiTraderPlan.None)),
                rows.Count(r => r.Action is not ("" or AiTraderPlan.None) && r.Allowed), rows.Count(r => !r.Allowed && r.Rule is not ("no-answer" or "unreadable")),
                rows.Count(r => r.Rule is "no-answer" or "unreadable")),
            latest,
            new AiTraderShadowDay(shadow.Count, shadow.Count(p => p.ExitUtc is null), AiTraderShadowBook.Net(shadow))));
    }

    /// <summary>
    /// The shadow book: a day's positions (IST) or a replay's, oldest first, with the net after charges (open
    /// ones as if sold at their marks).
    /// </summary>
    [HttpGet("positions")]
    public async Task<IActionResult> Positions([FromQuery] string? day, [FromQuery] long? replay, CancellationToken cancellationToken)
    {
        var date = IstTime.DateOf(_time.GetUtcNow().UtcDateTime);
        if (!string.IsNullOrWhiteSpace(day) && !DateOnly.TryParseExact(day, "yyyy-MM-dd", out date))
        {
            return BadRequest(new { error = "day is yyyy-MM-dd." });
        }

        var query = replay is long id
            ? db.AiTraderShadowPositions.Where(p => p.ReplaySessionId == id)
            : db.AiTraderShadowPositions.Where(p => p.ReplaySessionId == null && p.Day == date);
        var positions = await query.AsNoTracking().OrderBy(p => p.EntryUtc).ThenBy(p => p.Id).ToListAsync(cancellationToken);
        return Ok(new AiTraderShadowBookView(
            replay is null ? date.ToString("yyyy-MM-dd") : null, replay, positions.Count, positions.Count(p => p.ExitUtc is null),
            AiTraderShadowBook.Net(positions), positions.Sum(p => p.Charges), positions.Select(View).ToList()));
    }

    /// <summary>
    /// The AI Trader against the bar: each replay it decided in and each live shadow day, with its shadow book's net
    /// after charges next to the baseline rule's on the same day (<see cref="AiTraderBaselineScorer"/>) and doing
    /// nothing (₹0). The totals count every full row (looks from 09:30 or earlier to 14:30 or later) whose baseline is
    /// scored, listed or not: each replay counts, so a day replayed twice counts twice against the same rule result.
    /// <paramref name="take"/> limits only the rows listed, newest first; <c>RowsTotal</c> says how many there are.
    /// </summary>
    [HttpGet("scoreboard")]
    public async Task<IActionResult> Scoreboard([FromQuery] int take = 60, CancellationToken cancellationToken = default)
    {
        var looks = await db.AiTraderDecisions.AsNoTracking()
            .Where(d => d.Mode != AiTraderModes.Live)
            .GroupBy(d => new { d.ReplaySessionId, d.Day })
            .Select(g => new
            {
                g.Key.ReplaySessionId, g.Key.Day, First = g.Min(d => d.ClockUtc), Last = g.Max(d => d.ClockUtc), Looks = g.Count(),
                Actions = g.Count(d => d.Action != "" && d.Action != AiTraderPlan.None),
                NoAnswer = g.Count(d => d.Rule == "no-answer" || d.Rule == "unreadable"),
            })
            .ToListAsync(cancellationToken);
        var positions = await db.AiTraderShadowPositions.AsNoTracking().ToListAsync(cancellationToken);
        var baselines = await db.AiTraderBaselines.AsNoTracking()
            .Where(b => b.Rule == AiTraderBaselineScorer.TrendRule).ToDictionaryAsync(b => b.Day, cancellationToken);

        // Every row is built: the totals are over all of them, and take only limits the rows listed.
        var rows = looks
            .OrderByDescending(l => l.Day).ThenByDescending(l => l.ReplaySessionId ?? long.MaxValue)
            .Select(l =>
            {
                var book = positions.Where(p => p.ReplaySessionId == l.ReplaySessionId && (l.ReplaySessionId != null || p.Day == l.Day)).ToList();
                var first = TimeOnly.FromDateTime(IstTime.ToIst(l.First));
                var last = TimeOnly.FromDateTime(IstTime.ToIst(l.Last));
                decimal net = AiTraderShadowBook.Net(book);
                var baseline = baselines.GetValueOrDefault(l.Day);
                return new AiTraderScoreRow(
                    l.ReplaySessionId is null ? AiTraderModes.Shadow : AiTraderModes.Replay, l.ReplaySessionId, l.Day.ToString("yyyy-MM-dd"),
                    first.ToString("HH:mm"), last.ToString("HH:mm"), first <= FullFrom && last >= FullUntil, l.Looks, l.Actions, l.NoAnswer,
                    book.Count, book.Count(p => p.ExitUtc is null), net, book.Sum(p => p.Charges),
                    baseline is null ? null : Baseline(baseline), baseline is null ? null : net - baseline.NetPnl);
            })
            .ToList();

        var scored = rows.Where(r => r.Full && r.Baseline is not null).ToList();
        var totals = new AiTraderScoreTotals(
            scored.Count, scored.Sum(r => r.Net), scored.Sum(r => r.Baseline!.Net), scored.Count(r => r.Net > r.Baseline!.Net),
            scored.Count(r => r.Net > 0), scored.Count(r => r.Baseline!.Net > 0), scored.Sum(r => r.Positions), scored.Sum(r => r.Charges));
        return Ok(new AiTraderScoreboard(AiTraderBaselineScorer.TrendRule, BaselineRuleText, totals, rows.Take(Math.Clamp(take, 1, 365)).ToList(), rows.Count));
    }

    /// <summary>A day counts in the totals when its looks span the session: from 09:30 or earlier to 14:30 or later.</summary>
    private static readonly TimeOnly FullFrom = new(9, 30);

    private static readonly TimeOnly FullUntil = new(14, 30);

    public const string BaselineRuleText =
        "At 11:00 IST, NIFTY's 5-minute trend picks the side: last close above EMA 20 and EMA 50 with EMA 20 above, the at-the-money call; " +
        "below both with EMA 20 below, the put; otherwise no trade. One lot, stop 30% under the entry, target 50% over it, squared off at " +
        "15:30, with the shadow book's fills and charges.";

    private static AiTraderBaselineView Baseline(AiTraderBaseline b) => new(
        b.Rule, b.OptionType, b.Symbol, b.EntryUtc is DateTime e ? IstTime.ToIst(e).ToString("HH:mm") : null, b.EntryPrice,
        b.ExitUtc is DateTime x ? IstTime.ToIst(x).ToString("HH:mm") : null, b.ExitPrice, b.ExitReason, b.Charges, b.NetPnl, b.Note);

    private static AiTraderShadowPositionView View(AiTraderShadowPosition p) => new(
        p.Id, p.DecisionId, p.Mode, p.ReplaySessionId, p.Day.ToString("yyyy-MM-dd"), p.Symbol, p.Underlying, p.OptionType, p.Strike,
        p.Expiry.ToString("yyyy-MM-dd"), p.Lots, p.LotSize, p.EntryUtc, IstTime.ToIst(p.EntryUtc).ToString("HH:mm"), p.EntryPrice, p.StopLoss,
        p.Target, p.MarkPrice, p.MarkUtc, p.ExitUtc is null, p.ExitUtc, p.ExitUtc is DateTime x ? IstTime.ToIst(x).ToString("HH:mm") : null,
        p.ExitPrice, p.ExitReason, p.Charges, p.ExitUtc is null ? AiTraderShadowBook.Net([p]) : p.NetPnl ?? 0m);

    /// <summary>Decisions newest first: a day's (IST), a replay's (<paramref name="replay"/>), or the latest.</summary>
    [HttpGet("decisions")]
    public async Task<IActionResult> Decisions([FromQuery] string? day, [FromQuery] long? replay, [FromQuery] int take = 50,
        [FromQuery] long? beforeId = null, CancellationToken cancellationToken = default)
    {
        var query = db.AiTraderDecisions.AsNoTracking().AsQueryable();
        if (replay is long id) query = query.Where(d => d.ReplaySessionId == id);
        if (!string.IsNullOrWhiteSpace(day))
        {
            if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", out var date)) return BadRequest(new { error = "day is yyyy-MM-dd." });
            query = query.Where(d => d.Day == date);
        }

        if (beforeId is long before)
        {
            // The list is newest by clock, not by id: a day's list holds that day's replays too, decided on the same
            // clocks on later evenings with higher ids. The page goes on from the cursor's clock, then its id.
            var at = await db.AiTraderDecisions.AsNoTracking().Where(d => d.Id == before).Select(d => (DateTime?)d.ClockUtc).FirstOrDefaultAsync(cancellationToken);
            query = at is DateTime clock
                ? query.Where(d => d.ClockUtc < clock || (d.ClockUtc == clock && d.Id < before))
                : query.Where(d => d.Id < before);
        }

        var items = await Summaries(query, Math.Clamp(take, 1, 200), cancellationToken);
        return Ok(new { items, nextBeforeId = items.Count == Math.Clamp(take, 1, 200) ? items[^1].Id : (long?)null });
    }

    [HttpGet("decisions/{id:long}")]
    public async Task<IActionResult> Decision(long id, CancellationToken cancellationToken)
    {
        var d = await db.AiTraderDecisions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return d is null
            ? NotFound(new { error = $"No decision {id}." })
            : Ok(new AiTraderDecisionDetail(Summary(d), d.Brief, d.PlanJson, d.ResultJson, d.BriefHash));
    }

    private static async Task<List<AiTraderDecisionSummary>> Summaries(IQueryable<AiTraderDecision> query, int take, CancellationToken cancellationToken) =>
        (await query.OrderByDescending(d => d.ClockUtc).ThenByDescending(d => d.Id).Take(take).ToListAsync(cancellationToken))
        .Select(Summary)
        .ToList();

    private static AiTraderDecisionSummary Summary(AiTraderDecision d) => new(
        d.Id, d.ClockUtc, IstTime.ToIst(d.ClockUtc).ToString("HH:mm"), d.Day.ToString("yyyy-MM-dd"), d.Mode, d.ReplaySessionId, d.Action, d.Underlying,
        d.Reason, d.Confidence, d.Allowed, d.Rule, d.Why, d.Executed, d.Error, d.Model, d.CallId, OptionOf(d.PlanJson));

    /// <summary>The CE or PE a plan names, so a list can say "Buy NIFTY CE" without the plan; null when it names none.</summary>
    public static string? OptionOf(string planJson)
    {
        if (string.IsNullOrWhiteSpace(planJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(planJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("option", out var o)
                   && o.ValueKind == JsonValueKind.String && o.GetString() is "CE" or "PE"
                ? o.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record AiTraderStatus(string Status, string Mode, int EveryMinutes, AiTraderRules Rules, AiTraderDay Today,
    IReadOnlyList<AiTraderDecisionSummary> Latest, AiTraderShadowDay Shadow);

/// <summary>Today's shadow book: positions opened, still open, and the net after charges (open ones as if sold at their marks).</summary>
public sealed record AiTraderShadowDay(int Positions, int Open, decimal Net);

/// <summary>The rule, the totals over every full scored row, the newest rows (at most <c>take</c>) and how many rows there are in all.</summary>
public sealed record AiTraderScoreboard(string Rule, string RuleText, AiTraderScoreTotals Totals, IReadOnlyList<AiTraderScoreRow> Rows, int RowsTotal);

/// <summary>
/// Over every full, scored row, listed or not: the AI's net and the baseline's (both after charges), the rows the AI beat
/// it on, and the rows each made money on. <c>Days</c> (and the "days" counts) are rows: each replay and each live shadow
/// day counts once, so a day replayed twice counts twice, against the same rule result. The name is kept for the console.
/// </summary>
public sealed record AiTraderScoreTotals(int Days, decimal AiNet, decimal BaselineNet, int AiBeatBaseline, int AiPositiveDays, int BaselinePositiveDays,
    int Trades, decimal Charges);

/// <summary>One replay (<c>Kind</c> replay) or live shadow day (<c>Kind</c> shadow). <c>VsBaseline</c> is the AI's net less the baseline's.</summary>
public sealed record AiTraderScoreRow(string Kind, long? ReplaySessionId, string Day, string FirstIst, string LastIst, bool Full, int Looks, int Actions,
    int NoAnswer, int Positions, int Open, decimal Net, decimal Charges, AiTraderBaselineView? Baseline, decimal? VsBaseline);

/// <summary>The baseline rule on that day: the side it took (empty: no trade), the contract, in and out, and its net after charges.</summary>
public sealed record AiTraderBaselineView(string Rule, string OptionType, string Symbol, string? EntryIst, decimal? EntryPrice, string? ExitIst,
    decimal? ExitPrice, string ExitReason, decimal Charges, decimal Net, string Note);

public sealed record AiTraderShadowBookView(string? Day, long? Replay, int Positions, int Open, decimal Net, decimal Charges,
    IReadOnlyList<AiTraderShadowPositionView> Items);

/// <summary>
/// One shadow position. <c>Net</c> is after charges: as closed, or for an open one as if sold at its mark now.
/// <c>ExitReason</c> is stop, target, exit (its own decision), close (the session's close) or replay-ended.
/// </summary>
public sealed record AiTraderShadowPositionView(
    long Id, long DecisionId, string Mode, long? ReplaySessionId, string Day, string Symbol, string Underlying, string OptionType, decimal Strike,
    string Expiry, int Lots, int LotSize, DateTime EntryUtc, string EntryIst, decimal EntryPrice, decimal StopLoss, decimal Target,
    decimal? MarkPrice, DateTime? MarkUtc, bool Open, DateTime? ExitUtc, string? ExitIst, decimal? ExitPrice, string ExitReason,
    decimal Charges, decimal Net);

/// <summary>Today's looks: all of them, those that proposed an action, those allowed, those refused by a rule, and those with no usable answer.</summary>
public sealed record AiTraderDay(string Date, int Decisions, int Actions, int Allowed, int Refused, int NoAnswer);

public sealed record AiTraderDecisionSummary(
    long Id, DateTime ClockUtc, string ClockIst, string Day, string Mode, long? ReplaySessionId, string Action, string Underlying,
    string Reason, double? Confidence, bool Allowed, string Rule, string Why, bool Executed, string Error, string Model, long? CallId,
    string? Option = null);

public sealed record AiTraderDecisionDetail(AiTraderDecisionSummary Decision, string Brief, string PlanJson, string ResultJson, string BriefHash);
