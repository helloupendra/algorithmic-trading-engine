namespace AlgoTrading.Contracts.MutualFunds;

/// <summary>Schemes sharing one value of a field (a plan, an option, a category, an AMC).</summary>
public class MutualFundCountDto
{
    public string Key { get; set; } = string.Empty;
    public int Schemes { get; set; }

    /// <summary>Of those, schemes with a NAV within 30 days of the newest NAV day.</summary>
    public int Active { get; set; }
}

/// <summary>What <c>mf_nav</c> holds.</summary>
public class MutualFundNavCoverageDto
{
    /// <summary>NAV rows stored; TimescaleDB's estimate once the table is large (<see cref="RowsApproximate"/>).</summary>
    public long Rows { get; set; }

    public bool RowsApproximate { get; set; }
    public DateOnly? FirstDate { get; set; }
    public DateOnly? LastDate { get; set; }

    /// <summary>The newest day with NAVs for most schemes (not a Sunday with only the liquid funds').</summary>
    public DateOnly? LatestFullDate { get; set; }

    /// <summary>The newest business day whose NAVs should be stored by now.</summary>
    public DateOnly ExpectedDate { get; set; }

    /// <summary>Business days after <see cref="LatestFullDate"/> up to <see cref="ExpectedDate"/>; null when nothing is stored.</summary>
    public int? BusinessDaysBehind { get; set; }
}

/// <summary>The last daily sync.</summary>
public class MutualFundSyncDto
{
    public string Trigger { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public bool Ok { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateOnly? AsOfDate { get; set; }
    public int Rows { get; set; }
    public int NewSchemes { get; set; }
    public int NavRowsWritten { get; set; }
}

/// <summary>Where the NAV history backfill stands.</summary>
public class MutualFundBackfillDto
{
    public bool Enabled { get; set; }
    public bool Requested { get; set; }
    public bool Running { get; set; }

    /// <summary>"idle", "waiting", "running", "paused", "failed" or "done".</summary>
    public string State { get; set; } = "idle";

    /// <summary>The request being read now, e.g. "tp=1 2020-04-01..2020-06-29".</summary>
    public string? Now { get; set; }

    public DateOnly From { get; set; }
    public DateOnly? Top { get; set; }

    /// <summary>History is complete from this day to <see cref="Top"/>.</summary>
    public DateOnly? Cursor { get; set; }

    public int WindowsDone { get; set; }

    /// <summary>Windows still to fetch, at 90 days each.</summary>
    public int WindowsLeft { get; set; }

    public long RowsWritten { get; set; }
    public int? MaxWindows { get; set; }
    public string? LastWindow { get; set; }
    public string? LastError { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

/// <summary>GET api/MutualFunds/coverage: what the desk holds, before anything is picked.</summary>
public class MutualFundCoverageResponse
{
    public DateTime ServerUtc { get; set; }
    public int Schemes { get; set; }
    public int ActiveSchemes { get; set; }
    public List<MutualFundCountDto> ByPlan { get; set; } = new();
    public List<MutualFundCountDto> ByOption { get; set; } = new();
    public List<MutualFundCountDto> ByCategory { get; set; } = new();
    public List<MutualFundCountDto> ByAmc { get; set; } = new();
    public MutualFundNavCoverageDto Nav { get; set; } = new();
    public MutualFundSyncDto? LastSync { get; set; }
    public bool SyncEnabled { get; set; }
    public bool SyncRunning { get; set; }
    public MutualFundBackfillDto Backfill { get; set; } = new();

    /// <summary>What the fund report's jobs have collected: benchmarks, TER, the category ranking.</summary>
    public MutualFundAnalyticsCoverageDto Analytics { get; set; } = new();
}

/// <summary>One scheme in a search.</summary>
public class MutualFundSchemeDto
{
    public int SchemeCode { get; set; }
    public string SchemeName { get; set; } = string.Empty;
    public string Amc { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public string Option { get; set; } = string.Empty;
    public string? OptionLabel { get; set; }
    public string? IsinGrowth { get; set; }
    public string? IsinReinvest { get; set; }
    public DateOnly? FirstNavDate { get; set; }
    public DateOnly? LastNavDate { get; set; }
    public decimal? LastNav { get; set; }
    public bool IsActive { get; set; }
}

public class MutualFundNavPointDto
{
    public DateOnly Date { get; set; }
    public decimal Nav { get; set; }
}

/// <summary>GET api/MutualFunds/{code}/nav.</summary>
public class MutualFundNavResponse
{
    public MutualFundSchemeDto Scheme { get; set; } = new();
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public List<MutualFundNavPointDto> Rows { get; set; } = new();
}

/// <summary>GET api/MutualFunds/{code}/check: our NAVs against mfapi.in's for the same scheme.</summary>
public class MutualFundSpotCheckResponse
{
    public int SchemeCode { get; set; }
    public string Source { get; set; } = string.Empty;

    /// <summary>Days both hold, between our first and last stored day.</summary>
    public int Compared { get; set; }

    public int Matching { get; set; }

    /// <summary>Days the values differ, up to 20 of them.</summary>
    public List<MutualFundSpotCheckDiffDto> Different { get; set; } = new();
    public int DifferentCount { get; set; }

    /// <summary>Days inside our stored range that mfapi.in has and we do not.</summary>
    public int MissingHere { get; set; }

    /// <summary>Days we hold that mfapi.in does not.</summary>
    public int MissingThere { get; set; }

    public List<DateOnly> MissingHereSample { get; set; } = new();
}

public class MutualFundSpotCheckDiffDto
{
    public DateOnly Date { get; set; }
    public decimal Ours { get; set; }
    public decimal Theirs { get; set; }
}

/// <summary>The answer to starting, resuming or pausing the history backfill.</summary>
public class MutualFundBackfillRequestResponse
{
    /// <summary>False inside market hours on a trading day: it starts after the close.</summary>
    public bool StartsNow { get; set; }

    public string Message { get; set; } = string.Empty;
    public MutualFundBackfillDto Backfill { get; set; } = new();
}
