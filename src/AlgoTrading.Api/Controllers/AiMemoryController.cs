using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services.AgentMemory;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The agents' memory on the AI workspace: what each agent reads before it
/// answers, the owner's notes and verdicts, and the lessons waiting for them.
/// </summary>
/// <remarks>
/// Admin-only, like the rest of <c>api/Ai</c>. The rules (what is active at
/// once, what waits, what is masked) are <see cref="AiMemoryService"/>'s; this
/// shapes requests and answers. The contract is
/// <c>private/ai-workspace/MEMORY-CONTRACT.md</c>'s, documented in
/// <c>docs/modules/ai.md</c>.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/Ai")]
public class AiMemoryController(
    TradingDbContext db,
    AiMemoryService memory,
    AiSettingsStore store,
    IOptionsMonitor<AiSettings> settings) : ControllerBase
{
    /// <summary>The memories, newest first: all, one agent's, one status's; with the counts and the prompt budget.</summary>
    [HttpGet("memories")]
    public async Task<IActionResult> List([FromQuery] string? agent = null, [FromQuery] string? status = null, CancellationToken cancellationToken = default)
    {
        if (status is not null && !AiMemoryStatus.All.Contains(status)) return BadRequest(new { error = "status is active, proposed, rejected or retired." });

        var query = db.AiMemories.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(agent)) query = query.Where(m => m.AgentKey == agent);
        var all = await query.OrderByDescending(m => m.Id).ToListAsync(cancellationToken);

        var s = settings.CurrentValue;
        var counts = new AiMemoryCounts(
            all.Count(m => m.Status == AiMemoryStatus.Active),
            all.Count(m => m.Status == AiMemoryStatus.Proposed),
            all.Count(m => m.Status == AiMemoryStatus.Rejected),
            all.Count(m => m.Status == AiMemoryStatus.Retired));
        int activeChars = all.Where(m => m.Status == AiMemoryStatus.Active).Sum(m => m.Text.Length + m.Context.Length);

        // "on" is the agent's own switch: a switched-off agent's memories are kept but not read.
        var state = await store.LoadAsync(cancellationToken);
        return Ok(new AiMemoryList(
            s.MemoryEnabled,
            memory.Agents().Select(a => new AiMemoryAgent(a.Key, a.Name, state.Agent(a.Key)?.Enabled ?? false)).ToList(),
            counts,
            s.MemoryBudgetChars,
            activeChars,
            all.Where(m => status is null || m.Status == status).Select(ToDto).ToList()));
    }

    /// <summary>A note the owner writes: active at once.</summary>
    [HttpPost("memories")]
    public async Task<IActionResult> Add([FromBody] AiMemoryAdd? body, CancellationToken cancellationToken)
    {
        try
        {
            var saved = await memory.RememberAsync(body?.Agent, body?.Text, Actor(), "console", cancellationToken);
            return StatusCode(StatusCodes.Status201Created, ToDto(saved));
        }
        catch (AiMemoryException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Message });
        }
    }

    /// <summary>Edits a memory's text, or approves, rejects, retires or restores it.</summary>
    [HttpPut("memories/{id:long}")]
    public async Task<IActionResult> Update(long id, [FromBody] AiMemoryUpdate? body, CancellationToken cancellationToken)
    {
        if (body is null || (body.Text is null && body.Status is null)) return BadRequest(new { error = "Send text, status or both." });
        try
        {
            return Ok(ToDto(await memory.UpdateAsync(id, body.Text, body.Status, Actor(), cancellationToken)));
        }
        catch (AiMemoryException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Message });
        }
    }

    /// <summary>Whether memory helps: the daily check's score, the memories active, the owner's verdicts, by day.</summary>
    [HttpGet("memories/progress")]
    public async Task<IActionResult> Progress([FromQuery] int days = 30, CancellationToken cancellationToken = default)
    {
        var list = await memory.ProgressAsync(days, cancellationToken);
        return Ok(new AiMemoryProgress(list.Select(d => new AiMemoryDayDto(d.Date, d.CheckPassed, d.CheckTotal, d.Score, d.ActiveMemories, d.Ups, d.Downs)).ToList()));
    }

    /// <summary>The owner's 👍 or 👎 on an answer (0 takes it back), with what it should have said.</summary>
    [HttpPost("calls/{id:long}/feedback")]
    public async Task<IActionResult> Feedback(long id, [FromBody] AiFeedbackRequest? body, CancellationToken cancellationToken)
    {
        if (body is null) return BadRequest(new { error = "Send score: 1, -1 or 0." });
        try
        {
            var (call, saved) = await memory.FeedbackAsync(id, body.Score, body.Correction, Actor(), "console", cancellationToken);
            return Ok(new AiFeedbackResult(call.Id, call.FeedbackScore ?? 0, call.FeedbackNote, saved is null ? null : ToDto(saved)));
        }
        catch (AiMemoryException ex)
        {
            return StatusCode(ex.Status, new { error = ex.Message });
        }
    }

    public static AiMemoryDto ToDto(AiMemory m)
    {
        var review = AiMemoryService.Review(m);
        return new AiMemoryDto(
            m.Id, m.AgentKey, AiCatalog.AgentName(m.AgentKey), m.Kind, m.Status, m.Text, m.Context, m.Source, m.Via,
            m.SourceCallId, m.SourceReportId, m.CreatedBy, Utc(m.CreatedUtc), m.DecidedBy, Utc(m.DecidedUtc), Utc(m.ActivatedUtc),
            Utc(m.RetiredUtc), Utc(m.UpdatedUtc), m.Uses, Utc(m.LastUsedUtc), m.Ups, m.Downs, m.CheckPasses, m.CheckFails,
            review.Review, review.Reason);
    }

    private string Actor()
    {
        string by = User.GetUserName() ?? User.Identity?.Name ?? "admin";
        return by.Length > 100 ? by[..100] : by;
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is null ? null : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);
}

// ---------- wire shapes (camelCase on the wire) ------------------------------

public sealed record AiMemoryDto(
    long Id,
    string AgentKey,
    string AgentName,
    string Kind,
    string Status,
    string Text,
    string Context,
    string Source,
    string Via,
    long? SourceCallId,
    long? SourceReportId,
    string CreatedBy,
    DateTime CreatedUtc,
    string DecidedBy,
    DateTime? DecidedUtc,
    DateTime? ActivatedUtc,
    DateTime? RetiredUtc,
    DateTime UpdatedUtc,
    int Uses,
    DateTime? LastUsedUtc,
    int Ups,
    int Downs,
    int CheckPasses,
    int CheckFails,
    bool Review,
    string? ReviewReason);

public sealed record AiMemoryAgent(string Key, string Name, bool On);

public sealed record AiMemoryCounts(int Active, int Proposed, int Rejected, int Retired);

public sealed record AiMemoryList(
    bool Enabled,
    IReadOnlyList<AiMemoryAgent> Agents,
    AiMemoryCounts Counts,
    int BudgetChars,
    int ActiveChars,
    IReadOnlyList<AiMemoryDto> Memories);

public sealed record AiMemoryAdd(string? Agent, string? Text);

public sealed record AiMemoryUpdate(string? Text, string? Status);

public sealed record AiFeedbackRequest(int Score, string? Correction);

public sealed record AiFeedbackResult(long CallId, int Score, string Note, AiMemoryDto? Memory);

public sealed record AiMemoryDayDto(string Date, int? CheckPassed, int? CheckTotal, double? Score, int ActiveMemories, int Ups, int Downs);

public sealed record AiMemoryProgress(IReadOnlyList<AiMemoryDayDto> Days);
