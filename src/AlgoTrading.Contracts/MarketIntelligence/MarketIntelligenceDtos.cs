namespace AlgoTrading.Contracts.MarketIntelligence;

/// <summary>A page of rows, with the total the filter matched.</summary>
public class PagedResponse<T>
{
    public int Total { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
    public List<T> Items { get; set; } = [];
}

/// <summary>A recorded headline. <see cref="FirstSeenUtc"/> is when the desk first saw it.</summary>
public class NewsHeadlineDto
{
    public long Id { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public DateTime? PublishedUtc { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public decimal? Sentiment { get; set; }
    public short? Importance { get; set; }
    public string Symbols { get; set; } = string.Empty;
    public string Topics { get; set; } = string.Empty;
    public DateTime? ScoredUtc { get; set; }
    public string ScoreModel { get; set; } = string.Empty;
}

/// <summary>A recorded exchange filing.</summary>
public class AnnouncementDto
{
    public long Id { get; set; }
    public string Exchange { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string AttachmentUrl { get; set; } = string.Empty;
    public DateTime? AnnouncedUtc { get; set; }
    public DateTime FirstSeenUtc { get; set; }
    public decimal? Sentiment { get; set; }
    public short? Importance { get; set; }
    public string Symbols { get; set; } = string.Empty;
    public string Topics { get; set; } = string.Empty;
    public DateTime? ScoredUtc { get; set; }
    public string ScoreModel { get; set; } = string.Empty;
}

/// <summary>A board meeting announced in advance.</summary>
public class CalendarEventDto
{
    public long Id { get; set; }
    public string Exchange { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public DateOnly EventDate { get; set; }
    public DateTime FirstSeenUtc { get; set; }
}

/// <summary>One overseas daily bar, dated in its own market's time zone.</summary>
public class GlobalDailyBarDto
{
    public string Symbol { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public decimal? Open { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public decimal Close { get; set; }
    public decimal? Volume { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTime FetchedUtc { get; set; }
}

/// <summary>One price in one morning snapshot.</summary>
public class QuoteSnapshotDto
{
    public string Key { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal? PreviousClose { get; set; }
    public decimal? ChangePct { get; set; }
    public DateTime? AsOfUtc { get; set; }
    public DateTime FetchedUtc { get; set; }
    public string Source { get; set; } = string.Empty;
}

/// <summary>One NSE session's breadth.</summary>
public class BreadthDayDto
{
    public DateOnly Date { get; set; }
    public int Advances { get; set; }
    public int Declines { get; set; }
    public int Unchanged { get; set; }
    public int Traded { get; set; }
    public decimal? TurnoverCr { get; set; }
    public int? Highs52w { get; set; }
    public int? Lows52w { get; set; }
    public string Source { get; set; } = string.Empty;
}

/// <summary>How the recorders and backfills stand. The desk checkup reads this.</summary>
public class MarketIntelligenceStatusResponse
{
    public DateTime ServerUtc { get; set; }

    /// <summary>When this API process started; the in-memory fields below cover only the time since.</summary>
    public DateTime StartedUtc { get; set; }

    public List<RecorderStatusDto> Recorders { get; set; } = [];
    public List<BackfillStatusDto> Backfills { get; set; } = [];
    public List<GlobalSymbolCoverageDto> GlobalSymbols { get; set; } = [];
}

public class RecorderStatusDto
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    /// <summary>When it runs, in words.</summary>
    public string Schedule { get; set; } = string.Empty;

    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public string? LastMessage { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorUtc { get; set; }

    /// <summary>Sources failing right now ("BBC World: HTTP 503"); one down feed is not the recorder down.</summary>
    public List<string> FailingSources { get; set; } = [];

    /// <summary>Rows it wrote since 00:00 IST today (for breadth: whether today's session row exists, 0 or 1).</summary>
    public int RowsToday { get; set; }

    /// <summary>The newest row's stamp in the table, which survives an API restart where the fields above do not.</summary>
    public DateTime? LatestRowUtc { get; set; }

    /// <summary>
    /// It should have succeeded by now and has not: enabled, inside its
    /// window, and no success for longer than its schedule allows.
    /// </summary>
    public bool Overdue { get; set; }
}

public class BackfillStatusDto
{
    public string Dataset { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    /// <summary>"idle", "running", "waiting" (for the market hours to end), or "done".</summary>
    public string State { get; set; } = string.Empty;

    public DateOnly From { get; set; }
    public DateOnly? FirstDate { get; set; }
    public DateOnly? LastDate { get; set; }
    public int Days { get; set; }

    /// <summary>NSE sessions from <see cref="From"/> to the latest published one with nothing stored and no known reason; null where it does not apply (overseas markets keep their own holidays).</summary>
    public int? MissingSessions { get; set; }

    /// <summary>Days the source has no file for (holidays the calendar did not know).</summary>
    public int NoFileDays { get; set; }

    public bool Requested { get; set; }
    public int? RemainingAtStart { get; set; }
    public int StoredThisRun { get; set; }
    public DateTime? LastStartedUtc { get; set; }
    public DateTime? LastFinishedUtc { get; set; }
    public string? LastMessage { get; set; }
    public string? LastError { get; set; }
}

public class GlobalSymbolCoverageDto
{
    public string Symbol { get; set; } = string.Empty;
    public string SourceSymbol { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateOnly? FirstDate { get; set; }
    public DateOnly? LastDate { get; set; }
    public int Rows { get; set; }
}

/// <summary>The answer to "backfill this now".</summary>
public class BackfillRequestResponse
{
    public List<string> Datasets { get; set; } = [];

    /// <summary>False inside market hours on a trading day: the request waits until 15:40 IST.</summary>
    public bool StartsNow { get; set; }

    public string Message { get; set; } = string.Empty;
}
