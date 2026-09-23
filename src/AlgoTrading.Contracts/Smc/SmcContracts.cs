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

/// <summary>The candle behind the impulse that broke a level: where price is said to have been left from.</summary>
/// <remarks>
/// The block is confirmed by the break, not by its own candle, so it appears
/// several candles after it happened and often after price has already left the
/// zone. That lag is the honest reading and it is the same bargain the swing lag
/// makes; the popular scripts hide it by drawing the box at its own candle.
/// </remarks>
public sealed class SmcOrderBlockDto
{
    /// <summary>"bullish" or "bearish": the direction of the break that confirmed it.</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>The higher edge of the zone, whichever way the block faces.</summary>
    public decimal Top { get; set; }

    /// <summary>The lower edge. Wick to wick, so the zone holds every price that candle traded.</summary>
    public decimal Bottom { get; set; }

    /// <summary>The block's own candle: where the box starts.</summary>
    public DateTime TimeUtc { get; set; }

    /// <summary>The candle that broke structure. Nothing is drawn before it.</summary>
    public DateTime ConfirmedTimeUtc { get; set; }

    /// <summary>When price came back for it and used it up, or null while it still stands.</summary>
    public DateTime? MitigatedTimeUtc { get; set; }
}

/// <summary>A band of price the candles either side of a fast move left untouched.</summary>
/// <remarks>
/// Read geometrically — the three candles simply do not overlap — so no rule is
/// imposed on the middle candle's body. Every fast three-bar move prints one, and
/// no size threshold is applied unless <c>fvgMinSize</c> asks for one, so a chart
/// that draws them all will be crowded; drawing only the unfilled ones prunes
/// itself, because most gaps fill within a few candles.
/// </remarks>
public sealed class SmcFairValueGapDto
{
    /// <summary>"bullish" or "bearish": which way the move that left the gap ran.</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>The higher edge of the band.</summary>
    public decimal Top { get; set; }

    /// <summary>The lower edge.</summary>
    public decimal Bottom { get; set; }

    /// <summary>The first of the three candles: where the band starts.</summary>
    public DateTime TimeUtc { get; set; }

    /// <summary>The third candle, the first that could know the gap. Nothing is drawn before it.</summary>
    public DateTime ConfirmedTimeUtc { get; set; }

    /// <summary>When price traded back into it, or null while it stands open.</summary>
    public DateTime? FilledTimeUtc { get; set; }
}

/// <summary>The stretch of candles one reading of the market held for, break to break.</summary>
/// <remarks>
/// This is order flow in the sense the SMC and ICT material use the phrase: which
/// way the market is being delivered, inferred from price alone. It is not
/// footprint order flow — bid/ask delta, cumulative delta, volume at price — which
/// needs trades classified by aggressor side that this platform's feed does not
/// carry. A chart drawing this must say which of the two it means.
/// </remarks>
public sealed class SmcOrderFlowRunDto
{
    /// <summary>"bullish" or "bearish": the direction of the break that started the run.</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>The break that started it. Nothing is drawn before it.</summary>
    public DateTime FromTimeUtc { get; set; }

    /// <summary>The break that ended it, or null while it still runs — draw it open to the right edge.</summary>
    public DateTime? ToTimeUtc { get; set; }

    /// <summary>When this leg's inducement was swept, which is where a break of structure became armed. Null until then.</summary>
    public DateTime? InducedTimeUtc { get; set; }
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

    /// <summary>"touch" (default), "midpoint" or "close": when a zone counts as used.</summary>
    public string Zones { get; set; } = string.Empty;

    /// <summary>Gaps narrower than this were not reported. Zero — no threshold — is the default.</summary>
    public decimal FvgMinSize { get; set; }

    /// <summary>
    /// True when only the zones still standing at the last candle were reported —
    /// the blocks price has not come back for, and the gaps it has not filled.
    /// Off by default, so the history comes back whole. Runs are never dropped by
    /// it; see the endpoint for why.
    /// </summary>
    public bool StandingZonesOnly { get; set; }

    public List<SmcCandleDto> Candles { get; set; } = [];
    public List<SmcSwingDto> Swings { get; set; } = [];
    public List<SmcEventDto> Events { get; set; } = [];
    public List<SmcInducementDto> Inducements { get; set; } = [];
    public List<SmcOrderBlockDto> OrderBlocks { get; set; } = [];
    public List<SmcFairValueGapDto> Gaps { get; set; } = [];
    public List<SmcOrderFlowRunDto> OrderFlowRuns { get; set; } = [];

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

    /// <summary>
    /// The timeframes above it, highest first, as state and line marks only —
    /// no candles, and no zones, which can only be drawn against candles of their own.
    /// </summary>
    public List<SmcStructureResponse> Higher { get; set; } = [];
}
