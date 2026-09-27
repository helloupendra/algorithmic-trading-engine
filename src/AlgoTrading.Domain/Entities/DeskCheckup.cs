namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One desk checkup: a health checklist Sentinel runs at fixed times (before
/// the open, after the close, at end of day, weekly) or when an admin asks,
/// with a plain-English "what to do" on every item.
/// </summary>
/// <remarks>
/// <para>
/// Sentinel writes and updates these rows straight into the table, like its
/// incidents, so the columns and their spelling are a contract with
/// <c>sentinel/checkup/store.py</c> (<c>DeskCheckupsTableContractTests</c>
/// reads that file and checks every name it uses). The API never writes a
/// report: it reads them, and inserts a <see cref="DeskCheckupStatus.Requested"/>
/// row when someone asks for a checkup now, which Sentinel picks up.
/// </para>
/// <para>
/// A row moves requested (on-request only) → running → done or failed. Every
/// text column is NOT NULL with an empty default rather than nullable, so a
/// Python insert that names only the columns it knows still lands.
/// </para>
/// </remarks>
public class DeskCheckup
{
    public long Id { get; set; }

    /// <summary>One of <see cref="DeskCheckupSlot"/>: which schedule it ran for, or on request.</summary>
    public string Slot { get; set; } = DeskCheckupSlot.OnRequest;

    /// <summary>One of <see cref="DeskCheckupStatus"/>.</summary>
    public string Status { get; set; } = DeskCheckupStatus.Requested;

    /// <summary>When someone asked for it; null for a scheduled checkup.</summary>
    public DateTime? RequestedUtc { get; set; }

    /// <summary>The user name of whoever asked; empty for a scheduled checkup.</summary>
    public string RequestedBy { get; set; } = string.Empty;

    public DateTime? StartedUtc { get; set; }

    public DateTime? CompletedUtc { get; set; }

    /// <summary>
    /// One of <see cref="DeskCheckupVerdict"/>, empty until the checkup is done.
    /// Sentinel sets it from the items (any fail is action, else any warn is
    /// attention, else ok); the API passes it through rather than recomputing
    /// it, so the console and Telegram can never disagree.
    /// </summary>
    public string Verdict { get; set; } = string.Empty;

    /// <summary>One sentence, for example "2 things to do before the open".</summary>
    public string Headline { get; set; } = string.Empty;

    /// <summary>
    /// A JSON array of items, each <c>{ key, area, title, state, detail, action, link? }</c>
    /// with <c>state</c> one of <see cref="DeskCheckupItemState"/>. Text rather
    /// than jsonb for the same reason as an incident's evidence: the writer is
    /// a script, and a malformed value should still be readable by a person.
    /// </summary>
    public string ItemsJson { get; set; } = "[]";

    /// <summary>Why the checkup failed; empty otherwise.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>When its Telegram message went out, if it did.</summary>
    public DateTime? NotifiedUtc { get; set; }

    /// <summary>The machine that ran it.</summary>
    public string Host { get; set; } = string.Empty;
}

/// <summary>The values <see cref="DeskCheckup.Status"/> takes, exactly as Sentinel writes them.</summary>
public static class DeskCheckupStatus
{
    /// <summary>Asked for from the console; Sentinel has not picked it up yet.</summary>
    public const string Requested = "requested";

    public const string Running = "running";

    public const string Done = "done";

    /// <summary>It could not finish, or nobody picked the request up; <see cref="DeskCheckup.Error"/> says which.</summary>
    public const string Failed = "failed";

    /// <summary>Not finished yet: a new request waits for one of these rather than adding another.</summary>
    public static readonly IReadOnlyList<string> Pending = new[] { Requested, Running };

    /// <summary>Over, one way or the other: what the console shows as the latest checkup.</summary>
    public static readonly IReadOnlyList<string> Finished = new[] { Done, Failed };

    /// <summary>All four. The table refuses any other spelling.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Requested, Running, Done, Failed };
}

/// <summary>The values <see cref="DeskCheckup.Slot"/> takes.</summary>
public static class DeskCheckupSlot
{
    public const string Morning = "morning";
    public const string Close = "close";
    public const string Night = "night";
    public const string Weekly = "weekly";
    public const string OnRequest = "on-request";
}

/// <summary>The values <see cref="DeskCheckup.Verdict"/> takes once a checkup is done.</summary>
public static class DeskCheckupVerdict
{
    public const string Ok = "ok";
    public const string Attention = "attention";
    public const string Action = "action";
}

/// <summary>The <c>state</c> of one item in <see cref="DeskCheckup.ItemsJson"/>.</summary>
public static class DeskCheckupItemState
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";
    public const string Info = "info";

    /// <summary>Not checked this time, for example a broker check on a holiday.</summary>
    public const string Skip = "skip";
}
