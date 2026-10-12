using System.Security.Cryptography;
using System.Text;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Ai;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.AiPhone;

/// <summary>A phone token as the console lists it: never the token, which is shown once, when it is made.</summary>
public sealed record IraPhoneTokenView(long Id, string Label, DateTime CreatedUtc, DateTime? LastUsedUtc);

/// <summary>A new phone token: its row, and the token itself, which exists only in this answer.</summary>
public sealed record IraPhoneTokenMade(IraPhoneTokenView Token, string Secret);

/// <summary>Who a phone token asks as: the token's row and its owner, an active admin as his account reads now.</summary>
public sealed record IraPhoneCaller(long TokenId, string Label, long UserId, string UserName);

/// <summary>
/// The phone tokens behind "Hey Siri, Ira": made and revoked by an admin on the console, each the key an Apple Shortcut
/// sends to <c>POST api/Ira/ask</c> (<see cref="Security.IraPhoneAuthentication"/>), and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// A token is <see cref="Prefix"/> and 32 random bytes in base64url (47 characters). Only its SHA-256 is stored: the
/// token is in the answer that made it and nowhere else, not in a table, a log line or the activity log. A token of 256
/// random bits needs no salt or slow hash: there is nothing to guess from its digest.
/// </para>
/// <para>
/// Each use reads the row again, so a revoke works at once, and reads the owner's account, so a token of an admin who
/// was disabled or is no longer an admin opens nothing. Each use stamps <see cref="AiPhoneToken.LastUsedUtc"/>.
/// </para>
/// </remarks>
public sealed class IraPhoneTokens(
    TradingDbContext db,
    IOptionsMonitor<AiSettings> settings,
    ILogger<IraPhoneTokens> logger,
    TimeProvider? time = null)
{
    /// <summary>Every phone token starts with this: the console's JWT handler leaves such a bearer alone.</summary>
    public const string Prefix = "ira_";

    /// <summary>The token's length: the prefix and 32 bytes in unpadded base64url.</summary>
    public const int Length = 47;

    public const int MaxLabel = 60;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>A new token: <see cref="Prefix"/> and 32 bytes from the system's cryptographic generator.</summary>
    public static string NewSecret() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>What is stored for a token: its SHA-256 as 64 lowercase hex digits.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Whether <paramref name="token"/> has a phone token's shape (prefix, length, base64url), before any lookup.</summary>
    public static bool IsShape(string? token) =>
        token is { Length: Length }
        && token.StartsWith(Prefix, StringComparison.Ordinal)
        && token.AsSpan(Prefix.Length).IndexOfAnyExcept(Base64Url) < 0;

    private static readonly System.Buffers.SearchValues<char> Base64Url =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    /// <summary>
    /// The bearer of an Authorization header when it is a phone token's (it starts with <see cref="Prefix"/>), whatever
    /// its shape; null for any other header, a console JWT included.
    /// </summary>
    public static string? FromAuthorization(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        string value = header.Trim();
        if (!value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        string bearer = value["Bearer ".Length..].Trim();
        return bearer.StartsWith(Prefix, StringComparison.Ordinal) ? bearer : null;
    }

    /// <summary>The admin's working tokens, newest first.</summary>
    public async Task<IReadOnlyList<IraPhoneTokenView>> ListAsync(long userId, CancellationToken cancellationToken) =>
        (await db.AiPhoneTokens.AsNoTracking()
            .Where(t => t.UserId == userId && t.RevokedUtc == null)
            .ToListAsync(cancellationToken))
        .OrderByDescending(t => t.CreatedUtc)
        .ThenByDescending(t => t.Id)
        .Select(View)
        .ToList();

    /// <summary>
    /// Makes a token for an active admin, labelled as he says ("Upendra's iPhone"). Refused, with the reason, when phone
    /// asks are switched off, the label is missing or too long, or he already holds <see cref="AiPhoneSettings.MaxTokensPerUser"/>.
    /// </summary>
    public async Task<(IraPhoneTokenMade? Made, string? Error)> CreateAsync(long userId, string userName, string? label, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue.Phone;
        if (!s.Enabled) return (null, OffText);

        string name = (label ?? string.Empty).Trim();
        if (name.Length == 0) return (null, "Name the phone, for example \"Upendra's iPhone\".");
        if (name.Length > MaxLabel) return (null, $"A phone's name is {MaxLabel} characters at most.");

        int held = await db.AiPhoneTokens.CountAsync(t => t.UserId == userId && t.RevokedUtc == null, cancellationToken);
        int cap = Math.Max(1, s.MaxTokensPerUser);
        if (held >= cap) return (null, $"You already have {held} phone tokens, the most the desk allows (Ai:Phone:MaxTokensPerUser). Revoke one first.");

        string secret = NewSecret();
        var row = new AiPhoneToken
        {
            UserId = userId,
            UserName = userName.Length > 100 ? userName[..100] : userName,
            Label = name,
            TokenHash = Hash(secret),
            CreatedUtc = Now(),
        };
        db.AiPhoneTokens.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Ira phone: {User} made phone token #{Id} ({Label})", row.UserName, row.Id, row.Label);
        return (new IraPhoneTokenMade(View(row), secret), null);
    }

    /// <summary>Revokes one of the admin's own tokens, at once; null when he has no such working token.</summary>
    public async Task<IraPhoneTokenView?> RevokeAsync(long userId, long id, CancellationToken cancellationToken)
    {
        var row = await db.AiPhoneTokens.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId && t.RevokedUtc == null, cancellationToken);
        if (row is null) return null;

        row.RevokedUtc = Now();
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Ira phone: {User} revoked phone token #{Id} ({Label})", row.UserName, row.Id, row.Label);
        return View(row);
    }

    /// <summary>
    /// The caller a token asks as, or null: not a token's shape, not known, revoked, or its owner is not an active admin
    /// now. A token that opens stamps its last use.
    /// </summary>
    public async Task<IraPhoneCaller?> AuthenticateAsync(string token, CancellationToken cancellationToken)
    {
        if (!IsShape(token)) return null;

        string hash = Hash(token);
        var row = await db.AiPhoneTokens.FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedUtc == null, cancellationToken);
        if (row is null) return null;

        var owner = await db.AppUsers.AsNoTracking()
            .Where(u => u.Id == row.UserId && u.IsActive && u.Role == UserRoles.Admin)
            .Select(u => new { u.Id, u.UserName })
            .FirstOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            logger.LogInformation("Ira phone: token #{Id} ({Label}) refused: its owner {User} is not an active admin", row.Id, row.Label, row.UserName);
            return null;
        }

        row.LastUsedUtc = Now();
        await db.SaveChangesAsync(cancellationToken);
        return new IraPhoneCaller(row.Id, row.Label, owner.Id, owner.UserName);
    }

    public const string OffText = "Ira on the phone is switched off on this desk (Ai:Phone:Enabled).";

    private static IraPhoneTokenView View(AiPhoneToken t) => new(
        t.Id, t.Label, DateTime.SpecifyKind(t.CreatedUtc, DateTimeKind.Utc), t.LastUsedUtc is DateTime used ? DateTime.SpecifyKind(used, DateTimeKind.Utc) : null);

    private DateTime Now() => _time.GetUtcNow().UtcDateTime;
}
