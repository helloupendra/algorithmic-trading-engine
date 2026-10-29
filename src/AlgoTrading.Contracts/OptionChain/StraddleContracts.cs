namespace AlgoTrading.Contracts.OptionChain;

/// <summary>One capture of the chain reduced to its at-the-money straddle.</summary>
public class StraddlePointResponse
{
    public DateTime CapturedUtc { get; set; }
    public decimal SpotPrice { get; set; }

    /// <summary>The strike nearest the spot; on an exact tie, the lower one (the chain page's rule).</summary>
    public decimal AtTheMoneyStrike { get; set; }

    /// <summary>The call's price: the mid of bid and ask when both are quoted, else the last trade. Null when neither.</summary>
    public decimal? CallPrice { get; set; }

    /// <summary>"mid", "ltp", or null when the call was not priced.</summary>
    public string? CallPriceBasis { get; set; }

    public decimal? PutPrice { get; set; }
    public string? PutPriceBasis { get; set; }

    /// <summary>Call plus put. Null when either leg is missing: never half a straddle.</summary>
    public decimal? StraddlePrice { get; set; }

    /// <summary>Mean of the ATM call and put IV in percent; either alone when the other is unpriced.</summary>
    public decimal? AtTheMoneyIv { get; set; }
}

/// <summary>
/// The at-the-money straddle of one underlying through one IST session, read
/// from the recorded chain of a single expiry: the nearest one on or after
/// that day, as the chain recorder chooses it.
/// </summary>
public class StraddleSeriesResponse
{
    public string Underlying { get; set; } = string.Empty;

    /// <summary>The IST session day the points belong to.</summary>
    public DateOnly Date { get; set; }

    /// <summary>The expiry every point was read from; null when nothing was recorded that day.</summary>
    public DateOnly? ExpiryDate { get; set; }

    /// <summary>IST calendar days from <see cref="Date"/> to the expiry; 0 on expiry day.</summary>
    public int? DaysToExpiry { get; set; }

    public bool IsToday { get; set; }

    /// <summary>Whether the underlying's market is trading now. Always false for a past day.</summary>
    public bool MarketOpen { get; set; }

    /// <summary>The server's clock when the answer was built, so ages are not at the mercy of the browser's clock.</summary>
    public DateTime ServerUtc { get; set; }

    public DateTime? FirstCapturedUtc { get; set; }
    public DateTime? LastCapturedUtc { get; set; }

    /// <summary>The recorders the day's captures came from ("dhan", "fyers").</summary>
    public List<string> Sources { get; set; } = new();

    /// <summary>Points whose two legs were both priced at the mid; the rest used a last trade for at least one leg.</summary>
    public int MidPricedPoints { get; set; }

    /// <summary>
    /// Today's lot size for the underlying, for a per-lot reading of the
    /// premium. It is today's, not necessarily the day's: the exchange revises
    /// lot sizes. Null when unknown.
    /// </summary>
    public int? LotSize { get; set; }

    /// <summary>One per capture minute, oldest first.</summary>
    public List<StraddlePointResponse> Points { get; set; } = new();
}

/// <summary>One recorded session's ATM IV, near the open and near the close.</summary>
public class IvHistoryDayResponse
{
    public DateOnly Date { get; set; }
    public DateOnly ExpiryDate { get; set; }
    public int DaysToExpiry { get; set; }

    /// <summary>ATM IV in percent at the first capture five minutes after the open (09:20 on NSE); null when none was priced.</summary>
    public decimal? OpenIv { get; set; }
    public DateTime? OpenCapturedUtc { get; set; }

    /// <summary>ATM IV at the last capture five minutes before the close (15:25 on NSE); null until then, or when none was priced.</summary>
    public decimal? CloseIv { get; set; }
    public DateTime? CloseCapturedUtc { get; set; }

    /// <summary>The recorder that produced the readings ("dhan", "fyers").</summary>
    public string? Source { get; set; }
}

/// <summary>
/// The underlying's ATM IV day by day, and where the current value sits
/// among the closes of the days before it.
/// </summary>
public class IvHistoryResponse
{
    public string Underlying { get; set; } = string.Empty;
    public DateTime ServerUtc { get; set; }

    /// <summary>Every recorded session in the last year, oldest first.</summary>
    public List<IvHistoryDayResponse> Days { get; set; } = new();

    /// <summary>The value ranked: today's newest ATM IV while today is recorded, else the newest session's close.</summary>
    public decimal? CurrentIv { get; set; }
    public DateOnly? CurrentDate { get; set; }
    public DateTime? CurrentCapturedUtc { get; set; }

    /// <summary>"latest" (today's newest capture) or "close" (the newest session's close).</summary>
    public string? CurrentBasis { get; set; }

    /// <summary>
    /// Where the current value sits between the lowest and highest close of
    /// the days used, 0 to 100. Null when those days span no range.
    /// </summary>
    public decimal? IvRank { get; set; }

    /// <summary>The share of the days used whose close was below the current value, 0 to 100.</summary>
    public decimal? IvPercentile { get; set; }

    /// <summary>
    /// The sessions the rank and percentile were measured over: closes before
    /// the current value's day. A year is about 250; fewer means the rank is
    /// over that many days only, and the console says so.
    /// </summary>
    public int DaysUsed { get; set; }

    /// <summary>The lowest and highest close of the days used, with the current value.</summary>
    public decimal? RangeLow { get; set; }
    public decimal? RangeHigh { get; set; }
}
