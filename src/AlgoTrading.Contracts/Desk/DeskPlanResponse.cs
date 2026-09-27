// src/AlgoTrading.Contracts/Desk/DeskPlanResponse.cs
namespace AlgoTrading.Contracts.Desk;

/// <summary>
/// The morning plan (config/morning-plan.txt) as the morning job reads it, and
/// each run it asks for against what is live now. Served by GET /api/Desk/plan.
/// </summary>
public class DeskPlanResponse
{
    /// <summary>The file read, as an absolute path.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>When the file was last changed.</summary>
    public DateTime ModifiedUtc { get; set; }

    /// <summary>The platform user names the plan is deployed into, in the file's order.</summary>
    public List<string> Accounts { get; set; } = new();

    public List<DeskPlanLine> Lines { get; set; } = new();

    /// <summary>Every run the plan asks for: account by account, line by line, underlying by underlying.</summary>
    public List<DeskPlanRun> Runs { get; set; } = new();

    /// <summary>How many runs the plan asks for.</summary>
    public int Planned { get; set; }

    /// <summary>How many of them are live now.</summary>
    public int Live { get; set; }

    /// <summary>Lines the morning job would read differently, or refuse; empty when the file is clean.</summary>
    public List<string> Warnings { get; set; } = new();
}

/// <summary>One strategy line of the plan.</summary>
public class DeskPlanLine
{
    /// <summary>1-based line number in the file.</summary>
    public int Number { get; set; }

    /// <summary>The line as written, without its comment.</summary>
    public string Text { get; set; } = string.Empty;

    public string Strategy { get; set; } = string.Empty;

    public List<string> Underlyings { get; set; } = new();

    public int Lots { get; set; }

    /// <summary>"default" (the job's own, 20 points unless configured), "none" ("-"), or "points".</summary>
    public string LegTarget { get; set; } = string.Empty;

    /// <summary>Premium points when <see cref="LegTarget"/> is "points".</summary>
    public decimal? LegTargetPoints { get; set; }

    /// <summary>The accounts an "@" list limits the line to; empty means every account.</summary>
    public List<string> OnlyAccounts { get; set; } = new();
}

/// <summary>One run the plan asks for, and whether it is running.</summary>
public class DeskPlanRun
{
    /// <summary>The platform user name, as the plan spells it.</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>The account's user id; null when no active account has that name.</summary>
    public long? UserId { get; set; }

    public string Strategy { get; set; } = string.Empty;

    public string Underlying { get; set; } = string.Empty;

    public int Lots { get; set; }

    /// <summary>
    /// A run of this strategy on this underlying is Running in this account
    /// with its runner process alive — the morning tally's test, so a row left
    /// Running by a dead runner is not counted.
    /// </summary>
    public bool IsLive { get; set; }

    /// <summary>That run's id (the newest, if a restart left two); null when none is live.</summary>
    public long? RunId { get; set; }
}
