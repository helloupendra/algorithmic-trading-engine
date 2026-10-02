namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One past moment of an index, for the AI Trader's base rates ("what happened after moments like this one"): what
/// was known at that moment, computed only from the bars up to it, and what followed. Built by code from the stored
/// 1-minute index candles, one row per underlying, day and slot (every 10 minutes from 09:25 to 15:05 IST). The
/// features are defined once, in <c>SituationMath</c>, for these rows and for the moment a brief is built at.
/// </summary>
/// <remarks>Percentages are in percent (0.25 is a quarter of one per cent), of the index at the moment unless said.</remarks>
public class AiTraderSituation
{
    public long Id { get; set; }

    /// <summary>NIFTY, BANKNIFTY or SENSEX.</summary>
    public string Underlying { get; set; } = string.Empty;

    public DateOnly Day { get; set; }

    /// <summary>The moment, IST: the bars that began before it (so had ended by it) are all it knew.</summary>
    public TimeOnly Slot { get; set; }

    public DateTime BuiltUtc { get; set; }

    /// <summary>The index at the moment: the last minute's close, the base of every outcome.</summary>
    public decimal Price { get; set; }

    // ---------- features ----------

    /// <summary>The move since the previous session's close.</summary>
    public double MovePrevClosePct { get; set; }

    /// <summary>The move since the day's open.</summary>
    public double MoveOpenPct { get; set; }

    /// <summary>The move over the last 30 minutes (since the open when the day is younger than that).</summary>
    public double Last30MinPct { get; set; }

    /// <summary>The day's high − low so far, as % of the previous close.</summary>
    public double RangePct { get; set; }

    /// <summary>The last 5-minute close against EMA 20 and EMA 50: <c>above</c> both, <c>below</c> both, or <c>between</c>.</summary>
    public string EmaSide { get; set; } = string.Empty;

    /// <summary>EMA 20 − EMA 50, as % of EMA 50.</summary>
    public double EmaGapPct { get; set; }

    /// <summary>India VIX at the moment; null when it was not recorded then.</summary>
    public double? Vix { get; set; }

    /// <summary>India VIX's change since its previous close; null when either is not recorded.</summary>
    public double? VixChangePct { get; set; }

    public int MinutesSinceOpen { get; set; }

    /// <summary>Trading days to the underlying's nearest option expiry (0 on the expiry day); null when no expiry is known near enough.</summary>
    public int? DaysToExpiry { get; set; }

    /// <summary>ISO weekday: 1 Monday … 5 Friday (6 for a Saturday special session).</summary>
    public int Weekday { get; set; }

    // ---------- outcomes ----------

    /// <summary>The return to 30 minutes later, or to 15:25 when that comes sooner. Null when the bars there are missing.</summary>
    public double? Return30MinPct { get; set; }

    /// <summary>The return to 60 minutes later, or to 15:25 when that comes sooner.</summary>
    public double? Return60MinPct { get; set; }

    /// <summary>The return to 15:25.</summary>
    public double? ReturnToClosePct { get; set; }

    /// <summary>The highest high from the moment to 15:25 against the price; 0 when it never rose above it.</summary>
    public double? MaxUpPct { get; set; }

    /// <summary>The lowest low from the moment to 15:25 against the price (≤ 0); 0 when it never fell below it.</summary>
    public double? MaxDownPct { get; set; }
}
