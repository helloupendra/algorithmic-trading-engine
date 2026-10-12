using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Api.Services.AiPhone;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Ira on the owner's phone, from the console's side: the phone tokens an Apple Shortcut asks with ("Hey Siri, Ira"),
/// made and revoked here, and the actions a phone question proposed, which the Ira app (/ira) shows for the PIN.
/// </summary>
/// <remarks>
/// Admin-only, as every AI endpoint is. Each admin sees and revokes his own tokens only. A token is shown once, in the
/// answer that made it; the desk keeps its SHA-256 (<see cref="IraPhoneTokens"/>). The Shortcut's own endpoint is
/// <see cref="IraPhoneController"/>, which the console's sign-in does not open.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/Ira")]
public sealed class IraController(
    IraPhoneTokens tokens,
    IraPhoneConversations conversations,
    TradingDbContext db,
    IOptionsMonitor<AiSettings> settings,
    TimeProvider? time = null) : ControllerBase
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The signed-in admin's phone tokens (never a token itself), newest first, and whether phone asks are on.</summary>
    [HttpGet("phones")]
    public async Task<IActionResult> Phones(CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue.Phone;
        var list = await tokens.ListAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(new IraPhonesView(s.Enabled, Math.Max(1, s.MaxTokensPerUser), list));
    }

    /// <summary>
    /// Makes a phone token for the signed-in admin, named as he asks. The answer holds the token (<c>token</c>), the only
    /// time it is ever shown: it is not stored, logged or kept in the activity log. 409 when phone asks are off, the
    /// name is missing or too long, or he already holds the most allowed.
    /// </summary>
    [HttpPost("phones")]
    public async Task<IActionResult> CreatePhone([FromBody] IraPhoneCreateRequest? body, CancellationToken cancellationToken)
    {
        var (made, error) = await tokens.CreateAsync(User.GetRequiredUserId(), User.GetUserName() ?? "admin", body?.Label, cancellationToken);
        if (made is null) return Conflict(new { error });

        HttpContext.Describe($"Made phone token #{made.Token.Id} ({made.Token.Label}) for Ira on the phone.", "ira-phone-token",
            made.Token.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Ok(new IraPhoneMadeView(made.Token.Id, made.Token.Label, made.Token.CreatedUtc, made.Token.LastUsedUtc, made.Secret));
    }

    /// <summary>Revokes one of the signed-in admin's phone tokens: refused from the next request on. 404 when it is not his, or already revoked.</summary>
    [HttpDelete("phones/{id:long}")]
    public async Task<IActionResult> RevokePhone(long id, CancellationToken cancellationToken)
    {
        var revoked = await tokens.RevokeAsync(User.GetRequiredUserId(), id, cancellationToken);
        if (revoked is null) return NotFound(new { error = $"No working phone token #{id} of yours." });

        conversations.Forget(id);
        HttpContext.Describe($"Revoked phone token #{id} ({revoked.Label}) for Ira on the phone.", "ira-phone-token",
            id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Ok(revoked);
    }

    /// <summary>
    /// The signed-in admin's actions proposed from his phone that still wait for the PIN, newest first: the Ira app shows
    /// each with the console's confirm card (<c>POST api/Ai/actions/{id}/confirm</c>).
    /// </summary>
    [HttpGet("actions")]
    public async Task<IActionResult> PhoneActions(CancellationToken cancellationToken)
    {
        long me = User.GetRequiredUserId();
        var now = _time.GetUtcNow().UtcDateTime;
        var rows = await db.AiActions.AsNoTracking()
            .Where(a => a.UserId == me
                        && a.Status == AiActionStatus.Pending
                        && a.Channel == AiActionChannel.Console
                        && a.ConversationId.StartsWith(IraPhoneConversations.Prefix)
                        && a.ExpiresUtc > now)
            .OrderByDescending(a => a.Id)
            .Take(10)
            .ToListAsync(cancellationToken);
        return Ok(rows.Select(a => new IraPhoneActionView(
            a.Id, a.Kind, a.Summary, DateTime.SpecifyKind(a.CreatedUtc, DateTimeKind.Utc), DateTime.SpecifyKind(a.ExpiresUtc, DateTimeKind.Utc), a.CallId)).ToList());
    }
}

public sealed record IraPhoneCreateRequest(string? Label);

/// <summary>The Phones box: whether phone asks are on, how many tokens one admin may hold, and his working tokens.</summary>
public sealed record IraPhonesView(bool Enabled, int MaxTokens, IReadOnlyList<IraPhoneTokenView> Tokens);

/// <summary>A token just made: its row and, this once, the token itself.</summary>
public sealed record IraPhoneMadeView(long Id, string Label, DateTime CreatedUtc, DateTime? LastUsedUtc, string Token);

/// <summary>An action a phone question proposed, waiting for the PIN.</summary>
public sealed record IraPhoneActionView(long Id, string Kind, string Summary, DateTime CreatedUtc, DateTime ExpiresUtc, long? CallId);
