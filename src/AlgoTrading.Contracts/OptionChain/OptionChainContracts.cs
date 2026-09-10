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
