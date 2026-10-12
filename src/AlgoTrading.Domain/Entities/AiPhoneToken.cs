namespace AlgoTrading.Domain.Entities;

/// <summary>
/// A phone's token for "Hey Siri, Ira" (12 Oct 2026): what an Apple Shortcut sends as <c>Authorization: Bearer …</c>
/// to <c>POST api/Ira/ask</c>, the one endpoint it opens. An admin makes one on the console, sees it once, and may revoke
/// it at any time.
/// </summary>
/// <remarks>
/// Only the SHA-256 of the token is kept (<see cref="TokenHash"/>), never the token: a copy of the table opens nothing.
/// A revoked token keeps its row, with <see cref="RevokedUtc"/>, so the console can say when it was revoked; it is
/// refused from that moment.
/// </remarks>
public class AiPhoneToken
{
    public long Id { get; set; }

    /// <summary>The console user the token asks as: an active admin, read again on every use.</summary>
    public long UserId { get; set; }

    public string UserName { get; set; } = string.Empty;

    /// <summary>What the owner called the phone ("Upendra's iPhone").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The token's SHA-256, as 64 lowercase hex digits. Unique.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    /// <summary>When it last opened <c>POST api/Ira/ask</c>; null until it is first used.</summary>
    public DateTime? LastUsedUtc { get; set; }

    /// <summary>When it was revoked; null while it works.</summary>
    public DateTime? RevokedUtc { get; set; }
}
