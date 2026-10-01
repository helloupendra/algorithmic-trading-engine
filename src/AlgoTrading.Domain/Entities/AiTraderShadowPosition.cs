namespace AlgoTrading.Domain.Entities;

/// <summary>
/// A position the AI Trader would hold had it placed its allowed buy: kept by code in shadow mode and in a
/// market replay, where nothing is placed. It is bought at the ask its decision saw, watched every minute
/// against its stop and target at the price it could be sold at, and squared off at the session's close, so a
/// shadow day can be scored after charges like a traded one.
/// </summary>
public class AiTraderShadowPosition
{
    public long Id { get; set; }

    public DateTime CreatedUtc { get; set; }

    /// <summary>The decision whose allowed buy opened it.</summary>
    public long DecisionId { get; set; }

    /// <summary><see cref="AiTraderModes.Shadow"/> or <see cref="AiTraderModes.Replay"/>.</summary>
    public string Mode { get; set; } = AiTraderModes.Shadow;

    public long? ReplaySessionId { get; set; }

    /// <summary>The IST day it was opened on (a replay's: the replayed day).</summary>
    public DateOnly Day { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public string Underlying { get; set; } = string.Empty;

    /// <summary>CE or PE.</summary>
    public string OptionType { get; set; } = string.Empty;

    public decimal Strike { get; set; }

    public DateOnly Expiry { get; set; }

    public int Lots { get; set; }

    public int LotSize { get; set; }

    /// <summary>The market's time it was bought at: now, or the replay's clock.</summary>
    public DateTime EntryUtc { get; set; }

    public decimal EntryPrice { get; set; }

    public decimal StopLoss { get; set; }

    public decimal Target { get; set; }

    /// <summary>The last traded price at the last check, else the price it could be sold at.</summary>
    public decimal? MarkPrice { get; set; }

    public DateTime? MarkUtc { get; set; }

    public DateTime? ExitUtc { get; set; }

    public decimal? ExitPrice { get; set; }

    /// <summary><c>stop</c>, <c>target</c>, <c>exit</c> (its own decision), <c>close</c> (the session's close) or <c>replay-ended</c>.</summary>
    public string ExitReason { get; set; } = string.Empty;

    /// <summary>The round trip's charges, once closed.</summary>
    public decimal Charges { get; set; }

    /// <summary>Profit after charges, once closed.</summary>
    public decimal? NetPnl { get; set; }

    public int Units => Lots * LotSize;
}
