namespace AlgoTrading.Domain.Entities;

/// <summary>
/// A problem Sentinel found on the desk, with its history: when it was first
/// seen, how often it has been seen since, and whether anyone has dealt with it.
/// </summary>
/// <remarks>
/// <para>
/// Sentinel (the Python watchman under <c>src/AlgoTrading.PythonEngine/sentinel</c>)
/// writes these rows straight into the table rather than through the API,
/// because the moment it most needs to record something is the moment the API
/// is not answering. The columns and their spelling are therefore a contract
/// with <c>sentinel/store.py</c>: a rename here is an insert that fails there,
/// at the worst possible time. The API only reads the rows and moves them
/// through acknowledged and resolved.
/// </para>
/// <para>
/// One row is one problem, not one sighting. Sentinel identifies a problem by
/// its <see cref="Fingerprint"/> (the rule and what it is about, never a count
/// or a time) and, while the row is still live, a new sighting updates it and
/// bumps <see cref="Occurrences"/>. Once the row is resolved, the same problem
/// coming back opens a new row, so the history keeps each episode separately —
/// except within 30 minutes of Sentinel itself resolving it (<see cref="ResolvedBy"/>
/// null): a flapping problem is one episode, so Sentinel reopens the row
/// (Status back to open, or acknowledged if it was, and ResolvedUtc cleared)
/// rather than opening one per flap. 28 Sep 2026, 13:06-13:28: a feed stalling
/// every few minutes opened a new CRITICAL incident, and two messages, per stall.
/// </para>
/// </remarks>
public class Incident
{
    public long Id { get; set; }

    /// <summary>
    /// The identity of the problem, for example <c>feed-silent:dhan</c>. At most
    /// one live (open or acknowledged) row per fingerprint.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>The Sentinel agent that raised it: <c>feeds</c>, <c>runs</c>, <c>host</c>…</summary>
    public string Agent { get; set; } = string.Empty;

    /// <summary>The agent's rule, for example <c>feed-reconnect-loop</c>.</summary>
    public string Rule { get; set; } = string.Empty;

    /// <summary>One of <see cref="IncidentSeverity"/>. It only ever rises while the row is live.</summary>
    public string Severity { get; set; } = IncidentSeverity.Low;

    /// <summary>One of <see cref="IncidentStatus"/>.</summary>
    public string Status { get; set; } = IncidentStatus.Open;

    /// <summary>One line a trader understands.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>What happened, since when, with the number. Replaced on every sighting.</summary>
    public string? Summary { get; set; }

    /// <summary>The component, file, symbol or run the problem is about.</summary>
    public string? Location { get; set; }

    /// <summary>
    /// A JSON array of short strings: the observations behind the latest
    /// sighting. Kept as text rather than jsonb because the writer is a Python
    /// script with no schema of its own, and a malformed value should still be
    /// readable by a person instead of refused by the database.
    /// </summary>
    public string? EvidenceJson { get; set; }

    /// <summary>The likely fix, or the first thing to do.</summary>
    public string? Suggestion { get; set; }

    /// <summary>How many checks have seen this problem while it was live.</summary>
    public int Occurrences { get; set; } = 1;

    public DateTime FirstSeenUtc { get; set; }

    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// When it stopped being live: Sentinel sets it after enough clean checks
    /// in a row, a person sets it from the console. Sentinel clears it again
    /// when it reopens a row it resolved itself less than 30 minutes before.
    /// </summary>
    public DateTime? ResolvedUtc { get; set; }

    /// <summary>
    /// The user name of whoever resolved it from the console; null when
    /// Sentinel resolved it itself because its checks came back clean. That is
    /// the difference between "it cleared" and "someone closed it while it was
    /// still happening", which the history must not blur.
    /// </summary>
    public string? ResolvedBy { get; set; }

    public DateTime? AcknowledgedUtc { get; set; }

    /// <summary>The user name of whoever acknowledged it.</summary>
    public string? AcknowledgedBy { get; set; }

    /// <summary>When Sentinel last sent it to Telegram, if it did.</summary>
    public DateTime? NotifiedUtc { get; set; }

    /// <summary>
    /// Why it happened, in a person's words; null until someone writes it.
    /// </summary>
    /// <remarks>
    /// This and <see cref="Resolution"/> and <see cref="FixRef"/> are the
    /// incident's knowledge record: what the desk learnt, kept on the episode
    /// it was learnt from, so the next episode of the same fingerprint can show
    /// what was done last time. Only the console writes them (on resolve, or
    /// from "Edit notes" at any time); Sentinel never does, and its upsert
    /// leaves them alone, so a sighting cannot overwrite a note. Stored already
    /// redacted.
    /// </remarks>
    public string? RootCause { get; set; }

    /// <summary>What was done about it; null until someone writes it.</summary>
    public string? Resolution { get; set; }

    /// <summary>Where the fix lives: a commit sha, a pull request or a doc link.</summary>
    public string? FixRef { get; set; }
}

/// <summary>The values <see cref="Incident.Status"/> takes, exactly as Sentinel writes them.</summary>
public static class IncidentStatus
{
    /// <summary>Found, and nobody has said they are on it.</summary>
    public const string Open = "open";

    /// <summary>A person has seen it. Sentinel still updates it while the problem lasts.</summary>
    public const string Acknowledged = "acknowledged";

    /// <summary>
    /// Over. A later sighting of the same problem opens a new row, or, within
    /// 30 minutes of Sentinel resolving it itself, reopens this one.
    /// </summary>
    public const string Resolved = "resolved";

    /// <summary>Statuses Sentinel still tracks: a new sighting updates the row instead of opening another.</summary>
    public static readonly IReadOnlyList<string> Live = new[] { Open, Acknowledged };

    /// <summary>All three. The table refuses any other spelling.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Open, Acknowledged, Resolved };
}

/// <summary>The values <see cref="Incident.Severity"/> takes, lowest first.</summary>
public static class IncidentSeverity
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Critical = "critical";

    /// <summary>All four, lowest first. The table refuses any other spelling.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Low, Medium, High, Critical };
}

/// <summary>
/// When Sentinel last finished a round of checks: one row, overwritten every
/// round.
/// </summary>
/// <remarks>
/// Sentinel writes a row only when it finds something, so without this an
/// empty incident list cannot be told apart from a watchman that has stopped
/// looking, and a dead Sentinel would read as a quiet desk. Sentinel upserts
/// the single row (<see cref="SingletonId"/>) at the end of every round, straight
/// into the table like the incidents themselves; the console warns when it
/// goes stale. Like <see cref="Incident"/>, the spelling of the table and its
/// columns is a contract with <c>sentinel/store.py</c>.
/// </remarks>
public class SentinelHeartbeat
{
    /// <summary>The only row's key. The table refuses any other.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>When the latest round of checks finished.</summary>
    public DateTime LastCheckUtc { get; set; }
}
