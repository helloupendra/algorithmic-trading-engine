// src/AlgoTrading.Contracts/Backtest/BacktestCoverageResponse.cs
namespace AlgoTrading.Contracts.Backtest;

/// <summary>
/// What historical data exists for an underlying, shown BEFORE the user picks
/// a resolution or a date range. Served by GET /api/Backtest/coverage.
/// </summary>
public class BacktestCoverageResponse
{
    public string Underlying { get; set; } = string.Empty;
    public string SpotSymbol { get; set; } = string.Empty;
    public int LotSize { get; set; }

    /// <summary>"master" | "configured" | "unknown"</summary>
    public string LotSizeSource { get; set; } = string.Empty;

    /// <summary>One row per allowed resolution ("1", "5", "15", "D"), in that order.</summary>
    public List<BacktestResolutionCoverage> Resolutions { get; set; } = new();

    /// <summary>Strategy-facing resolutions the run needs ("5m"), from the catalog entry plus the driver.</summary>
    public List<string> RequiredResolutions { get; set; } = new();

    /// <summary>Stored option candles for this underlying's CE/PE contracts.</summary>
    public BacktestOptionCoverage OptionCandles { get; set; } = new();

    /// <summary>True when a valid FYERS session exists, so a backfill is possible.</summary>
    public bool BrokerLinked { get; set; }

    public List<string> Notes { get; set; } = new();
}

/// <summary>Coverage of the spot symbol at one resolution.</summary>
public class BacktestResolutionCoverage
{
    /// <summary>Canonical code ("5").</summary>
    public string Resolution { get; set; } = string.Empty;

    /// <summary>Display label ("5m").</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>True when the strategy's data requirements or the driver resolution need this feed.</summary>
    public bool Required { get; set; }

    public long BarCount { get; set; }
    public DateTime? FirstUtc { get; set; }
    public DateTime? LastUtc { get; set; }

    /// <summary>Distinct IST calendar days with at least one bar.</summary>
    public int Sessions { get; set; }

    /// <summary>"backfill" (candles table) | "live" (live_bars, 1m only) | "none".</summary>
    public string Source { get; set; } = "none";

    /// <summary>True when the broker is linked and this resolution can be pulled from FYERS history.</summary>
    public bool Backfillable { get; set; }
}

/// <summary>Stored option-contract candles (any resolution) for an underlying.</summary>
public class BacktestOptionCoverage
{
    /// <summary>Distinct CE/PE symbols with at least one stored candle.</summary>
    public int Symbols { get; set; }
    public DateTime? FirstUtc { get; set; }
    public DateTime? LastUtc { get; set; }

    /// <summary>Expiry dates ("yyyy-MM-dd") of those contracts, ascending.</summary>
    public List<string> Expiries { get; set; } = new();
}
