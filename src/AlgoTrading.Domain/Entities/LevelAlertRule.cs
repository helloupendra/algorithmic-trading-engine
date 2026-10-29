namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One user's "tell me when": a price crossing or touching a level on a symbol
/// the platform quotes, or an option chain's put-call ratio going above or below
/// a level.
/// </summary>
/// <remarks>
/// <para>
/// Owned by one user, taken from the sign-in token. A trader reads and edits
/// only their own; an admin sees everyone's. Watched by the level-alert
/// service in the API, which reads prices the feeds already wrote and chains
/// the recorder already captured: a rule never makes a vendor call.
/// </para>
/// <para>
/// One-shot by default: the first crossing fires it and it stops
/// (<see cref="LevelAlertStatus.Fired"/>). A repeating rule fires, disarms, and
/// arms again only once the value has moved back from the level by
/// <see cref="RearmBy"/>, so a price sitting on its level fires once rather than
/// on every tick either side of it.
/// </para>
/// </remarks>
public class LevelAlertRule
{
    public long Id { get; set; }

    /// <summary>Who it belongs to. Never from a request body.</summary>
    public long UserId { get; set; }

    /// <summary>One of <see cref="LevelAlertKinds"/>.</summary>
    public string Kind { get; set; } = LevelAlertKinds.Price;

    /// <summary>
    /// For a price, the canonical symbol ("NSE:NIFTY50-INDEX", "MCX:CRUDEOIL26NOVFUT");
    /// for a PCR, the chain's underlying ("NIFTY"), always its nearest expiry.
    /// </summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>One of <see cref="LevelAlertConditions"/>; a PCR takes above or below only.</summary>
    public string Condition { get; set; } = LevelAlertConditions.Above;

    public decimal Level { get; set; }

    /// <summary>Fire every time it crosses (after re-arming), rather than once.</summary>
    public bool Repeat { get; set; }

    /// <summary>
    /// How far the value must move back from the level before a repeating rule
    /// can fire again: a percent of the level for a price, PCR points for a PCR.
    /// Null on a one-shot rule.
    /// </summary>
    public decimal? RearmBy { get; set; }

    /// <summary>The owner's own words, sent with the alert. Free text: escaped wherever it is shown.</summary>
    public string? Note { get; set; }

    /// <summary>The last IST day it may fire; null for no end.</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>
    /// Also send to Telegram's Desk System chat. Honoured only for an admin's
    /// rule: a trader's alerts stay in the console, because the desk has no
    /// Telegram chat per trader and the Desk System chat is the operator's.
    /// </summary>
    public bool Notify { get; set; } = true;

    /// <summary>One of <see cref="LevelAlertStatus"/>'s stored values.</summary>
    public string Status { get; set; } = LevelAlertStatus.Active;

    /// <summary>
    /// Whether the next crossing fires it. False on a repeating rule from its
    /// fire until the value has moved back by <see cref="RearmBy"/>. Stored, so
    /// an API restart does not re-fire a level the price is still sitting on.
    /// </summary>
    public bool Armed { get; set; } = true;

    public DateTime? LastFiredUtc { get; set; }

    public int FireCount { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When the owner (or an admin) last saved it. The service never sets it, so a save it did not see is detectable.</summary>
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>What a level alert watches.</summary>
public static class LevelAlertKinds
{
    /// <summary>The last traded price of one symbol.</summary>
    public const string Price = "price";

    /// <summary>The put-call ratio (total put OI over total call OI) of an underlying's nearest-expiry chain.</summary>
    public const string Pcr = "pcr";

    public static readonly IReadOnlyList<string> All = [Price, Pcr];
}

/// <summary>When a level alert fires.</summary>
public static class LevelAlertConditions
{
    /// <summary>From at or below the level to above it.</summary>
    public const string Above = "above";

    /// <summary>From at or above the level to below it.</summary>
    public const string Below = "below";

    /// <summary>Reaches the level from either side, or passes through it. Prices only.</summary>
    public const string Touch = "touch";

    public static readonly IReadOnlyList<string> All = [Above, Below, Touch];
}

/// <summary>A level alert's state as stored. "Expired" is not stored: it is read off <see cref="LevelAlertRule.ExpiresOn"/>.</summary>
public static class LevelAlertStatus
{
    public const string Active = "active";

    /// <summary>Stopped by its owner; resuming arms it again.</summary>
    public const string Paused = "paused";

    /// <summary>A one-shot rule that has fired. Re-activating it arms it again.</summary>
    public const string Fired = "fired";

    /// <summary>Not stored: an active or paused rule past its last day.</summary>
    public const string Expired = "expired";

    /// <summary>The values the column may hold.</summary>
    public static readonly IReadOnlyList<string> Stored = [Active, Paused, Fired];
}
