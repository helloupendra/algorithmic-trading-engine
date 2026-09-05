namespace AlgoTrading.Domain.Entities;

/// <summary>
/// An invitation for one person to create their own account.
/// </summary>
/// <remarks>
/// The admin decides <em>who</em> may join; the invitee chooses their own
/// password, so it never passes through the admin's hands or a chat message.
/// <para>
/// Only the token's hash is stored, for the same reason a password is hashed: a
/// database dump must not hand someone a working invite. The plaintext token is
/// shown to the admin once, at creation, and never again.
/// </para>
/// <para>
/// What makes this safe is not the invite itself but what it creates: a Trader
/// with no module grants and no strategy package, which can sign in and do
/// nothing until an admin says otherwise.
/// </para>
/// </remarks>
public class UserInvite
{
    public long Id { get; set; }

    /// <summary>SHA-256 of the token in the link. The token itself is never stored.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Who the invite is for. The account is created with this email.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>Suggested username; the invitee may change it when accepting.</summary>
    public string SuggestedUserName { get; set; } = string.Empty;

    /// <summary>Modules the new account gets on acceptance. Empty is allowed.</summary>
    public string ModuleKeysCsv { get; set; } = string.Empty;

    /// <summary>Strategy package the new account is put on, if any.</summary>
    public long? StrategyPackageId { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>An unused invite stops working after this.</summary>
    public DateTime ExpiresUtc { get; set; }

    /// <summary>Set when it is used; an invite works exactly once.</summary>
    public DateTime? AcceptedUtc { get; set; }

    /// <summary>The account it created.</summary>
    public long? AcceptedUserId { get; set; }

    /// <summary>Set when an admin cancels it before use.</summary>
    public DateTime? RevokedUtc { get; set; }

    public bool IsUsable(DateTime nowUtc)
        => AcceptedUtc is null && RevokedUtc is null && ExpiresUtc > nowUtc;
}
