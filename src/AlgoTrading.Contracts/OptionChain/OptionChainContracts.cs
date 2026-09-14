namespace AlgoTrading.Contracts.OptionChain;

/// <summary>One strike's row as the poller captured it.</summary>
public class OptionChainSnapshotRow
{
    public string Underlying { get; set; } = string.Empty;
    public DateOnly ExpiryDate { get; set; }
    public decimal StrikePrice { get; set; }
    public string OptionType { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public decimal SpotPrice { get; set; }
    public decimal? LastTradedPrice { get; set; }
    public decimal? BidPrice { get; set; }
    public decimal? AskPrice { get; set; }
    public long? Volume { get; set; }
    public long? OpenInterest { get; set; }

    /// <summary>
    /// The previous day's closing open interest, as the broker reports it.
    /// </summary>
    /// <remarks>
    /// The market's own baseline for "OI change" — and one the broker computes,
    /// so it survives a poller that started late or restarted mid-session, which
    /// a session-first-snapshot baseline does not.
    /// </remarks>
    public long? PreviousDayOpenInterest { get; set; }

    /// <summary>
    /// The day's move in the option's price, as the broker reports it.
    /// </summary>
    /// <remarks>
    /// Measured from the previous close — the same basis as
    /// <see cref="PreviousDayOpenInterest"/>. Taking both from the broker is
    /// what lets the build-up reading work from the poller's first round rather
    /// than waiting for a session of snapshots to compare against.
    /// </remarks>
    public decimal? PriceChange { get; set; }

    /// <summary>
    /// Black-Scholes implied volatility and greeks, computed by the poller from
    /// the spot, strike, time to expiry and last traded price on this row.
    /// </summary>
    /// <remarks>
    /// The broker's chain carries none of these. Until 2026-09-10 they were
    /// computed only for the live-quote table, which is overwritten on every
    /// tick — so "what was the delta at 10:30" had no answer anywhere. Stored
    /// with every snapshot they become a history. Null when the contract
    /// could not be priced (no trade, after the bell, IV did not converge).
    /// </remarks>
    public decimal? ImpliedVolatility { get; set; }
    public decimal? Delta { get; set; }
    public decimal? Gamma { get; set; }
    public decimal? Theta { get; set; }
    public decimal? Vega { get; set; }

    public string? SourceKey { get; set; }
}

public class StoreOptionChainRequest
{
    public List<OptionChainSnapshotRow> Rows { get; set; } = new();
}

/// <summary>One side of one strike, with everything derived already applied.</summary>
public class OptionChainLegResponse
{
    public string Symbol { get; set; } = string.Empty;
    public decimal? LastTradedPrice { get; set; }
    public decimal? PriceChange { get; set; }
    public decimal? PriceChangePercent { get; set; }
    public decimal? BidPrice { get; set; }
    public decimal? AskPrice { get; set; }
    public long? Volume { get; set; }
    public long? OpenInterest { get; set; }

    /// <summary>Change against the session's first snapshot, not the previous poll.</summary>
    public long? OpenInterestChange { get; set; }
    public decimal? OpenInterestChangePercent { get; set; }

    public decimal? ImpliedVolatility { get; set; }
    public decimal? Delta { get; set; }
    public decimal? Gamma { get; set; }
    public decimal? Theta { get; set; }
    public decimal? Vega { get; set; }

    /// <summary>"LongBuildUp", "ShortCovering", … or "Neutral".</summary>
    public string BuildUp { get; set; } = "Neutral";

    /// <summary>
    /// The open interest the change is measured from — the previous day's close
    /// as the broker reported it. Carried so a live OI can be re-measured
    /// against the same baseline the snapshot used.
    /// </summary>
    public long? OpenInterestBaseline { get; set; }

    /// <summary>
    /// True when LTP, bid/ask, volume and OI on this leg come from a live quote
    /// no older than the freshness limit, rather than the per-minute snapshot.
    /// IV and greeks are always the snapshot's.
    /// </summary>
    public bool IsLive { get; set; }

    /// <summary>When the live quote was written; null for a snapshot-only leg.</summary>
    public DateTime? QuoteUpdatedUtc { get; set; }
}

public class OptionChainStrikeResponse
{
    public decimal StrikePrice { get; set; }
    public bool IsAtTheMoney { get; set; }
    public OptionChainLegResponse? Call { get; set; }
    public OptionChainLegResponse? Put { get; set; }

    /// <summary>Put OI over call OI at this strike. Null when no calls are written.</summary>
    public decimal? PutCallRatio { get; set; }

    /// <summary>The same ratio on the day's OI CHANGE rather than its level.</summary>
    public decimal? PutCallRatioOfChange { get; set; }
}

public class OptionChainResponse
{
    public string Underlying { get; set; } = string.Empty;
    public DateOnly ExpiryDate { get; set; }

    /// <summary>The moment this chain describes — now, or the replay clock.</summary>
    public DateTime AsOfUtc { get; set; }

    public decimal SpotPrice { get; set; }
    public decimal? AtTheMoneyStrike { get; set; }
    public decimal? MaxPainStrike { get; set; }

    /// <summary>Total put OI over total call OI across the chain.</summary>
    public decimal? PutCallRatio { get; set; }

    public long TotalCallOpenInterest { get; set; }
    public long TotalPutOpenInterest { get; set; }

    /// <summary>Where the most calls are written — the market's defended ceiling.</summary>
    public decimal? HeaviestCallStrike { get; set; }

    /// <summary>And the floor.</summary>
    public decimal? HeaviestPutStrike { get; set; }

    public List<OptionChainStrikeResponse> Strikes { get; set; } = new();

    /// <summary>
    /// True when this chain carries no open interest for the period asked for.
    /// </summary>
    /// <remarks>
    /// Open interest began being recorded only when the chain poller was added,
    /// so a backtest of an earlier session has prices and volume but no OI —
    /// and every column derived from it is blank rather than zero. The flag lets
    /// the console say so instead of drawing an empty chart that looks like a
    /// market with no open positions.
    /// </remarks>
    public bool OpenInterestUnavailable { get; set; }

    /// <summary>
    /// The reading strip above the chain: spot, future, VIX and the chain's own
    /// summary, each with where it came from. Filled only by the view endpoint;
    /// null on the plain chain read.
    /// </summary>
    public OptionChainHeaderResponse? Header { get; set; }

    /// <summary>Total day's change in call and put OI across the chain.</summary>
    public long TotalCallOpenInterestChange { get; set; }
    public long TotalPutOpenInterestChange { get; set; }
}

/// <summary>One price in the header strip, and how much to trust it.</summary>
public class OptionChainQuoteResponse
{
    public string Symbol { get; set; } = string.Empty;
    public decimal? LastPrice { get; set; }

    /// <summary>Against <see cref="PreviousClose"/>; null when no previous close is known.</summary>
    public decimal? Change { get; set; }
    public decimal? ChangePercent { get; set; }
    public decimal? PreviousClose { get; set; }

    /// <summary>
    /// Where the previous close came from: "feed" (the tick's own previous-close
    /// field), "dhan-close-field" (the close field of Dhan's quote packet, which
    /// carries the previous close during the session) or null when unknown.
    /// </summary>
    public string? PreviousCloseBasis { get; set; }

    /// <summary>When this price was true: the quote's write time, or the snapshot's capture time.</summary>
    public DateTime? AsOfUtc { get; set; }

    public string? SourceKey { get; set; }

    /// <summary>True only for a live quote no older than the freshness limit.</summary>
    public bool IsLive { get; set; }

    /// <summary>
    /// "live-quote", "last-quote" (older than the limit), "snapshot" (the
    /// capture's own spot) or "bar" (a replay reading the future's one-minute bar).
    /// </summary>
    public string Basis { get; set; } = "snapshot";
}

public class OptionChainFutureResponse : OptionChainQuoteResponse
{
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>Future minus spot, when both are known.</summary>
    public decimal? PremiumOverSpot { get; set; }
    public decimal? PremiumPercent { get; set; }
}

public class OptionChainHeaderResponse
{
    /// <summary>"live" (at least one fresh quote overlaid), "snapshot" or "replay".</summary>
    public string Mode { get; set; } = "snapshot";

    /// <summary>The server's clock when the answer was built, so ages are not at the mercy of the browser's clock.</summary>
    public DateTime ServerUtc { get; set; }

    public string Exchange { get; set; } = string.Empty;
    public bool MarketOpen { get; set; }

    /// <summary>
    /// The index for NSE/BSE underlyings. For an MCX commodity there is no spot:
    /// this is the future its options are written on.
    /// </summary>
    public OptionChainQuoteResponse? Spot { get; set; }

    /// <summary>True when <see cref="Spot"/> is a futures contract (MCX).</summary>
    public bool SpotIsFuture { get; set; }

    /// <summary>The nearest future of an index underlying; null for MCX, where the spot already is one.</summary>
    public OptionChainFutureResponse? Future { get; set; }

    /// <summary>India VIX; null when no quote for it exists.</summary>
    public OptionChainQuoteResponse? Vix { get; set; }

    public decimal? AtTheMoneyStrike { get; set; }
    public decimal? MaxPainStrike { get; set; }
    public decimal? PutCallRatio { get; set; }

    /// <summary>Put OI change over call OI change; null unless both sides added contracts.</summary>
    public decimal? PutCallRatioOfChange { get; set; }

    /// <summary>The strike with the most put OI — where writers defend the floor.</summary>
    public decimal? SupportStrike { get; set; }
    public long? SupportOpenInterest { get; set; }

    /// <summary>The strike with the most call OI — the defended ceiling.</summary>
    public decimal? ResistanceStrike { get; set; }
    public long? ResistanceOpenInterest { get; set; }

    public long TotalCallOpenInterest { get; set; }
    public long TotalPutOpenInterest { get; set; }
    public long TotalCallOpenInterestChange { get; set; }
    public long TotalPutOpenInterestChange { get; set; }

    /// <summary>Mean of the ATM call and put IV (either alone when one is missing).</summary>
    public decimal? AtTheMoneyIv { get; set; }

    /// <summary>IST calendar days from the chain's moment to expiry; 0 on expiry day.</summary>
    public int? DaysToExpiry { get; set; }

    public int? LotSize { get; set; }

    /// <summary>"master", "configured" or "unknown".</summary>
    public string? LotSizeSource { get; set; }

    /// <summary>The highest strike at or below spot, and the lowest above it — where the spot line sits.</summary>
    public decimal? SpotBetweenLower { get; set; }
    public decimal? SpotBetweenUpper { get; set; }

    public DateTime? SnapshotCapturedUtc { get; set; }
    public string? SnapshotSourceKey { get; set; }

    /// <summary>The newest live quote overlaid on the chain; null when none was fresh.</summary>
    public DateTime? LiveOverlayUtc { get; set; }
    public string? LiveSourceKey { get; set; }

    /// <summary>Legs carrying a fresh live quote, of all legs in the chain.</summary>
    public int LiveLegs { get; set; }
    public int TotalLegs { get; set; }

    /// <summary>The freshness limit a quote must meet to be shown as live.</summary>
    public int FreshSeconds { get; set; }
}

/// <summary>One capture of the whole chain, reduced to the numbers a session trend plots.</summary>
public class OptionChainTrendPointResponse
{
    public DateTime CapturedUtc { get; set; }
    public decimal SpotPrice { get; set; }
    public long CallOpenInterest { get; set; }
    public long PutOpenInterest { get; set; }
    public long CallOpenInterestChange { get; set; }
    public long PutOpenInterestChange { get; set; }
    public decimal? PutCallRatio { get; set; }
}

public class OptionChainTrendResponse
{
    public string Underlying { get; set; } = string.Empty;
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>The IST session day the points belong to.</summary>
    public DateOnly? SessionDate { get; set; }

    /// <summary>Captures on that day before thinning; the points are at most a few hundred of them.</summary>
    public int Captures { get; set; }

    public List<OptionChainTrendPointResponse> Points { get; set; } = new();
}

/// <summary>One strike's intraday history — the OI-change curves.</summary>
public class OptionChainSeriesPointResponse
{
    public DateTime CapturedUtc { get; set; }
    public decimal SpotPrice { get; set; }
    public decimal? CallLastTradedPrice { get; set; }
    public decimal? PutLastTradedPrice { get; set; }
    public long? CallOpenInterest { get; set; }
    public long? PutOpenInterest { get; set; }
    public long? CallOpenInterestChange { get; set; }
    public long? PutOpenInterestChange { get; set; }

    /// <summary>Put minus call OI change — the line the multi-OI page plots in blue.</summary>
    public long? OpenInterestChangeDifference { get; set; }

    public long? CallVolume { get; set; }
    public long? PutVolume { get; set; }
}

public class OptionChainSeriesResponse
{
    public string Underlying { get; set; } = string.Empty;
    public DateOnly ExpiryDate { get; set; }
    public decimal StrikePrice { get; set; }
    public List<OptionChainSeriesPointResponse> Points { get; set; } = new();
    public bool OpenInterestUnavailable { get; set; }
}

/// <summary>
/// One open paper position on a contract of the chain's underlying: a strategy
/// run's leg or a manual trade, so the chain shows what is being held on it.
/// </summary>
public class OptionChainPositionResponse
{
    public long RunId { get; set; }

    /// <summary>"Manual" for the manual book, otherwise the strategy's name.</summary>
    public string StrategyName { get; set; } = string.Empty;
    public bool IsManual { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string GroupId { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;

    /// <summary>"CE", "PE" or "FUT" (or the instrument type for anything else).</summary>
    public string InstrumentType { get; set; } = string.Empty;
    public decimal? StrikePrice { get; set; }
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>"LONG" or "SHORT".</summary>
    public string Direction { get; set; } = string.Empty;

    /// <summary>Lots for a derivative.</summary>
    public int Quantity { get; set; }
    public int LotSize { get; set; }
    public decimal AveragePrice { get; set; }

    /// <summary>The newest price known: the live quote when there is one, else the engine's last mark.</summary>
    public decimal? MarkPrice { get; set; }
    public DateTime? MarkUtc { get; set; }
    public decimal? UnrealizedPnl { get; set; }
    public decimal? StopLossPrice { get; set; }
    public decimal? TargetPrice { get; set; }
    public DateTime OpenedUtc { get; set; }
}
