using System;
using System.Collections.Generic;

namespace AlgoTrading.Contracts.MarketData;

/// <summary>
/// What the daily candle archive did for one IST trading day.
/// </summary>
/// <remarks>
/// The archive is what turns a day of live data into history that outlives
/// the tick retention window: every symbol that ticked gets 1, 5 and 15
/// minute candles built from its live 1-minute bars (source "live"), and the
/// index symbols additionally get the broker's own candles for the day, which
/// take precedence wherever both exist.
/// </remarks>
public class CandleArchiveResult
{
    public DateOnly Day { get; set; }
    public DateTime RanAtUtc { get; set; }

    /// <summary>Symbols that had live 1-minute bars on the day.</summary>
    public int SymbolsWithLiveBars { get; set; }

    /// <summary>Live 1-minute bars read.</summary>
    public int LiveBarsRead { get; set; }

    /// <summary>Candles written from the live bars, per resolution code ("1", "5", "15").</summary>
    public Dictionary<string, int> CandlesInserted { get; set; } = new();
    public Dictionary<string, int> CandlesUpdated { get; set; } = new();

    /// <summary>Rows left alone because another source (the broker) already owns them.</summary>
    public int CandlesOwnedElsewhere { get; set; }

    /// <summary>Broker backfills attempted for the index symbols: "SYMBOL/RES: n candles" or the failure.</summary>
    public List<string> BrokerBackfills { get; set; } = new();

    public List<string> Errors { get; set; } = new();
}
