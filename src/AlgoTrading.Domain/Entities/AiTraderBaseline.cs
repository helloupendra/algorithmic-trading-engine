namespace AlgoTrading.Domain.Entities;

/// <summary>
/// What a simple fixed rule made on a recorded day, scored by code from that day's recorded ticks with the
/// shadow book's fills and charges: the bar the AI Trader has to clear. One row per day and rule, computed once
/// the day is over; a day the rule does not trade is kept too, with a net of zero and why.
/// </summary>
public class AiTraderBaseline
{
    public long Id { get; set; }

    public DateTime ComputedUtc { get; set; }

    public DateOnly Day { get; set; }

    /// <summary>The rule's key, e.g. <c>nifty-trend-1100</c>.</summary>
    public string Rule { get; set; } = string.Empty;

    public string Underlying { get; set; } = string.Empty;

    /// <summary>CE, PE, or empty when the rule did not trade that day.</summary>
    public string OptionType { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;

    public int Lots { get; set; }

    public int LotSize { get; set; }

    public DateTime? EntryUtc { get; set; }

    public decimal? EntryPrice { get; set; }

    public decimal? StopLoss { get; set; }

    public decimal? Target { get; set; }

    public DateTime? ExitUtc { get; set; }

    public decimal? ExitPrice { get; set; }

    /// <summary><c>stop</c>, <c>target</c> or <c>close</c>; empty when it did not trade.</summary>
    public string ExitReason { get; set; } = string.Empty;

    public decimal Charges { get; set; }

    public decimal NetPnl { get; set; }

    /// <summary>Why it traded or did not, in a sentence.</summary>
    public string Note { get; set; } = string.Empty;
}
