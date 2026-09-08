namespace AlgoTrading.Contracts.Instruments;

/// <summary>
/// One FYERS symbol master (NSE_CM, NSE_FO, BSE_CM, BSE_FO, MCX_COM): the file
/// on this host, what the database holds for its exchange and segment, and
/// how the last refresh went.
/// </summary>
public class InstrumentMasterStatus
{
    /// <summary>File stem FYERS publishes, e.g. "MCX_COM".</summary>
    public string Name { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string Segment { get; set; } = string.Empty;
    /// <summary>What the master covers, for the reader.</summary>
    public string Label { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    public bool FilePresent { get; set; }
    public long? FileBytes { get; set; }
    public DateTime? FileModifiedUtc { get; set; }

    /// <summary>Instruments in the database for this exchange + segment (all rows, expired included).</summary>
    public int RowsInDb { get; set; }
    /// <summary>Of those, contracts that have not expired (cash rows never expire).</summary>
    public int ActiveRowsInDb { get; set; }

    public InstrumentMasterRefreshResult? LastRefresh { get; set; }
}

/// <summary>What one master's download + import did.</summary>
public class InstrumentMasterRefreshResult
{
    public string Name { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public bool Ok { get; set; }
    /// <summary>Why it failed; null when it did not.</summary>
    public string? Error { get; set; }
    public long? DownloadedBytes { get; set; }
    public int TotalRowsRead { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public string? Message { get; set; }
    /// <summary>Who pressed the button.</summary>
    public string? By { get; set; }
}

/// <summary>The refresh job: at most one runs at a time.</summary>
public class InstrumentMasterJob
{
    public bool IsRunning { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    /// <summary>Master being downloaded or imported right now.</summary>
    public string? Current { get; set; }
    public string? StartedBy { get; set; }
    public List<InstrumentMasterRefreshResult> Results { get; set; } = new();
}

public class InstrumentMastersResponse
{
    public string Directory { get; set; } = string.Empty;
    public List<InstrumentMasterStatus> Masters { get; set; } = new();
    public InstrumentMasterJob Job { get; set; } = new();
}
