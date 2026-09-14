namespace AlgoTrading.Domain.Enums;

/// <summary>
/// Which part of a trading day a holiday closes. NSE and BSE close whole days;
/// MCX publishes its holidays per session and often keeps one of its two open.
/// </summary>
public enum MarketClosure
{
    /// <summary>No session that day.</summary>
    FullDay = 0,

    /// <summary>MCX only: 09:00–17:00 closed, the evening session trades.</summary>
    MorningSession = 1,

    /// <summary>MCX only: the morning session trades, the evening session is closed.</summary>
    EveningSession = 2
}
