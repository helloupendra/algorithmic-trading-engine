// src/AlgoTrading.Contracts/Strategies/OpenPositionsResponse.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// Every open leg the caller may see, across strategy runs and manual books,
/// on every underlying. Served by GET /api/Positions/open.
/// </summary>
public class OpenPositionsResponse
{
    /// <summary>When the marks were read; each position's <c>MarkAgeSeconds</c> is counted to here.</summary>
    public DateTime AsOfUtc { get; set; }

    /// <summary>By account, then run, then the order the legs were opened.</summary>
    public List<OpenPositionResponse> Positions { get; set; } = new();
}

/// <summary>
/// One open leg, marked as its run card marks it: at the latest live quote, or
/// at its stored mark when no quote is known — with the age of whichever it
/// is, so yesterday's price reads as yesterday's.
/// </summary>
public class OpenPositionResponse
{
    public long PositionId { get; set; }

    public long RunId { get; set; }

    public string StrategyName { get; set; } = string.Empty;

    /// <summary>The owner's manual book rather than a strategy run.</summary>
    public bool IsManualBook { get; set; }

    /// <summary>The account the leg is held in.</summary>
    public long UserId { get; set; }

    public string? UserName { get; set; }

    public string GroupId { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;

    public string Underlying { get; set; } = string.Empty;

    public DateOnly? ExpiryDate { get; set; }

    public decimal? Strike { get; set; }

    /// <summary>"CE" or "PE"; empty for a future or a share.</summary>
    public string OptionType { get; set; } = string.Empty;

    /// <summary>"NIFTY 24500 CE · 29 Sep"; the symbol itself for anything that is not an option.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>"LONG" (bought) or "SHORT" (sold).</summary>
    public string Direction { get; set; } = string.Empty;

    public int Lots { get; set; }

    /// <summary>The lot size the leg's fills were booked at (1 for a share).</summary>
    public int LotSize { get; set; }

    /// <summary>Lots × lot size.</summary>
    public int Quantity { get; set; }

    public decimal EntryPrice { get; set; }

    /// <summary>The latest live quote, else the stored mark; null when neither exists.</summary>
    public decimal? MarkPrice { get; set; }

    /// <summary>When that price was written.</summary>
    public DateTime? MarkUtc { get; set; }

    /// <summary>Seconds from <see cref="MarkUtc"/> to <see cref="OpenPositionsResponse.AsOfUtc"/>.</summary>
    public long? MarkAgeSeconds { get; set; }

    /// <summary>At <see cref="MarkPrice"/>, before charges; null while no mark exists.</summary>
    public decimal? UnrealizedPnl { get; set; }

    /// <summary>The carry-forward tick: held overnight instead of squared off at the close.</summary>
    public bool CarryForward { get; set; }

    /// <summary>On a manual-book leg a strategy carried forward at the close: the run it came from.</summary>
    public long? CarriedFromRunId { get; set; }

    /// <summary>The strategy of <see cref="CarriedFromRunId"/>.</summary>
    public string? CarriedFromStrategy { get; set; }

    /// <summary>The leg's own stop-loss price, when the order that opened it carried one.</summary>
    public decimal? StopLossPrice { get; set; }

    /// <summary>The leg's own target price, when the order that opened it carried one.</summary>
    public decimal? TargetPrice { get; set; }

    public DateTime OpenedUtc { get; set; }

    /// <summary>IV and greeks, per unit and in rupees for this leg; null when no source could price it.</summary>
    public PositionGreeksResponse? Greeks { get; set; }
}
