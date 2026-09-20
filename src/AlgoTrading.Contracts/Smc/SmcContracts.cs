namespace AlgoTrading.Contracts.Smc;

/// <summary>One candle of the series the marks were read from.</summary>
public sealed class SmcCandleDto
{
    public DateTime TimestampUtc { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }

    /// <summary>True for a candle that came from today's live bars rather than from stored history.</summary>
    public bool Live { get; set; }
}

/// <summary>A swing point: where it is, and how it compares with the previous swing of its kind.</summary>
public sealed class SmcSwingDto
{
    /// <summary>"high" or "low".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>"HH", "HL", "LH", "LL", or null for the first swing of its kind.</summary>
    public string? Label { get; set; }

    public decimal Price { get; set; }
    public DateTime TimeUtc { get; set; }

    /// <summary>The candle that made this swing a structural point. Nothing is drawn before it.</summary>
    public DateTime ConfirmedTimeUtc { get; set; }

    /// <summary>
    /// True for a swing the structure turned on — a level whose break was, or
    /// would be, a BOS or a CHoCH. The rest are pullbacks inside a leg.
    /// </summary>
    public bool Major { get; set; }
}

/// <summary>A level giving way: "BOS" (the trend carried on) or "CHOCH" (it turned).</summary>
public sealed class SmcEventDto
{
    public string Kind { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal Level { get; set; }

    /// <summary>The swing that made the level: where the line starts.</summary>
    public DateTime LevelTimeUtc { get; set; }

    /// <summary>The candle that broke it: where the line ends.</summary>
    public DateTime BreakTimeUtc { get; set; }

    public decimal BreakPrice { get; set; }
}

/// <summary>The pullback inside a leg whose stops sit under (or over) it.</summary>
public sealed class SmcInducementDto
{
    public string Kind { get; set; } = string.Empty;
    public decimal Level { get; set; }
    public DateTime TimeUtc { get; set; }

    /// <summary>When a candle traded through it, or null if it was never taken.</summary>
    public DateTime? SweptTimeUtc { get; set; }

    /// <summary>Where the leg ended for an inducement that was never taken; null while it still stands.</summary>
    public DateTime? EndedTimeUtc { get; set; }
}

/// <summary>The marks for one symbol and timeframe, with the candles they were read from.</summary>
public sealed class SmcStructureResponse
{
    public string Symbol { get; set; } = string.Empty;
    public string Resolution { get; set; } = string.Empty;

    /// <summary>The rules used, so a chart can say what it is showing.</summary>
    public string Method { get; set; } = string.Empty;
    public int Strength { get; set; }
    public string BreakOn { get; set; } = string.Empty;

    public List<SmcCandleDto> Candles { get; set; } = [];
    public List<SmcSwingDto> Swings { get; set; } = [];
    public List<SmcEventDto> Events { get; set; } = [];
    public List<SmcInducementDto> Inducements { get; set; } = [];

    /// <summary>"none", "bullish" or "bearish" as at the last candle.</summary>
    public string Trend { get; set; } = string.Empty;

    /// <summary>Breaking this turns the trend: the protected low of a rally, or the protected high of a fall.</summary>
    public decimal? ProtectedLevel { get; set; }

    /// <summary>The leg's extreme so far; a close through it is a BOS once the inducement is taken.</summary>
    public decimal? BreakLevel { get; set; }

    /// <summary>Whether this leg's inducement has been swept, which is what a BOS waits for.</summary>
    public bool InducementTaken { get; set; }

    /// <summary>The inducement still standing, if any.</summary>
    public decimal? InducementLevel { get; set; }

    /// <summary>"first" (taught) or "last" (what the popular indicators do).</summary>
    public string Inducement { get; set; } = string.Empty;

    /// <summary>How many candles came from today's live bars.</summary>
    public int LiveCandles { get; set; }

    /// <summary>Stored candles stamped outside the trading session, which were left out.</summary>
    public int DroppedOutsideSession { get; set; }

    /// <summary>Said in words when there is nothing to read: no candles stored, too few to turn.</summary>
    public string? Note { get; set; }
}

/// <summary>
/// The nested reading: one chart's candles with the structure of that timeframe
/// and of the higher ones above it, each read from its own closed candles.
/// </summary>
public sealed class SmcLadderResponse
{
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The timeframe being drawn: the only one that carries candles.</summary>
    public SmcStructureResponse? Chart { get; set; }

    /// <summary>The timeframes above it, highest first, as state and marks only.</summary>
    public List<SmcStructureResponse> Higher { get; set; } = [];
}
