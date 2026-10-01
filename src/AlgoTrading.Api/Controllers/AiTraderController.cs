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

        return Ok(new AiTraderStatus(
            state.Agent(AiCatalog.AiTrader)?.Status ?? "off",
            s.AiTraderExecute ? AiTraderModes.Live : AiTraderModes.Shadow,
            s.AiTraderEveryMinutes,
            AiTraderAgent.Rules,
            new AiTraderDay(today.ToString("yyyy-MM-dd"), rows.Count, rows.Count(r => r.Action is not ("" or AiTraderPlan.None)),
                rows.Count(r => r.Action is not ("" or AiTraderPlan.None) && r.Allowed), rows.Count(r => !r.Allowed && r.Rule is not ("no-answer" or "unreadable")),
                rows.Count(r => r.Rule is "no-answer" or "unreadable")),
            latest));
    }

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

        if (beforeId is long before) query = query.Where(d => d.Id < before);
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
        d.Reason, d.Confidence, d.Allowed, d.Rule, d.Why, d.Executed, d.Error, d.Model, d.CallId);
}

public sealed record AiTraderStatus(string Status, string Mode, int EveryMinutes, AiTraderRules Rules, AiTraderDay Today, IReadOnlyList<AiTraderDecisionSummary> Latest);

/// <summary>Today's looks: all of them, those that proposed an action, those allowed, those refused by a rule, and those with no usable answer.</summary>
public sealed record AiTraderDay(string Date, int Decisions, int Actions, int Allowed, int Refused, int NoAnswer);

public sealed record AiTraderDecisionSummary(
    long Id, DateTime ClockUtc, string ClockIst, string Day, string Mode, long? ReplaySessionId, string Action, string Underlying,
    string Reason, double? Confidence, bool Allowed, string Rule, string Why, bool Executed, string Error, string Model, long? CallId);

public sealed record AiTraderDecisionDetail(AiTraderDecisionSummary Decision, string Brief, string PlanJson, string ResultJson, string BriefHash);
