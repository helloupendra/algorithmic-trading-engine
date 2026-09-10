using System;
using System.Collections.Generic;

namespace AlgoTrading.Contracts.MarketData;

/// <summary>
/// The market at a glance: the three index levels, the large-cap names that
/// drive them, and the three commodities — with the last saved quote for each.
/// </summary>
/// <remarks>
/// This is what a trader sees first after signing in. It is the platform's
/// fixed universe, not the trader's watchlist: the same for everyone, always
/// subscribed on the feed, never something they have to set up.
/// </remarks>
public class MarketPulseResponse
{
    public List<MarketPulseGroup> Groups { get; set; } = new();

    /// <summary>The newest quote stamp across the whole pulse; null when nothing has ticked yet.</summary>
    public DateTime? LatestQuoteUtc { get; set; }
}

public class MarketPulseGroup
{
    /// <summary>"index", "equity" or "commodity".</summary>
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<MarketPulseItem> Items { get; set; } = new();
}

public class MarketPulseItem
{
    public string Symbol { get; set; } = string.Empty;

    /// <summary>What a person calls it: "NIFTY 50", "Reliance", "Crude oil".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>For a future, which contract is being shown ("Sep 2026"); null otherwise.</summary>
    public string? Contract { get; set; }

    public decimal? LastTradedPrice { get; set; }
    public decimal? PreviousClose { get; set; }
    public decimal? Open { get; set; }
    public decimal? High { get; set; }
    public decimal? Low { get; set; }
    public long? Volume { get; set; }

    /// <summary>Last price minus the previous close; null when either is missing.</summary>
    public decimal? Change { get; set; }
    public decimal? ChangePercent { get; set; }

    public DateTime? UpdatedUtc { get; set; }

    /// <summary>False when the feed has not been told to carry this symbol yet, so the quote will never refresh.</summary>
    public bool IsSubscribed { get; set; }
}
