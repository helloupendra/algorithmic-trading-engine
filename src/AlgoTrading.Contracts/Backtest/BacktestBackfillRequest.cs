// src/AlgoTrading.Contracts/Backtest/BacktestBackfillRequest.cs
namespace AlgoTrading.Contracts.Backtest;

/// <summary>
/// Body of POST /api/Backtest/backfill: pull the underlying's spot candles from
/// FYERS history for the given resolutions and IST date range, in ≤ 30-day chunks.
/// </summary>
public class BacktestBackfillRequest
{
    public string Underlying { get; set; } = string.Empty;

    /// <summary>Canonical codes, e.g. ["5", "1"].</summary>
    public List<string> Resolutions { get; set; } = new();

    /// <summary>"yyyy-MM-dd" IST.</summary>
    public string FromDate { get; set; } = string.Empty;
    public string ToDate { get; set; } = string.Empty;
}

/// <summary>Result of POST /api/Backtest/backfill.</summary>
public class BacktestBackfillResponse
{
    public List<BacktestBackfillResolutionResult> PerResolution { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

/// <summary>Backfill outcome for one resolution.</summary>
public class BacktestBackfillResolutionResult
{
    /// <summary>Canonical code ("5").</summary>
    public string Resolution { get; set; } = string.Empty;

    /// <summary>Candles returned by FYERS across the fetched chunks.</summary>
    public int CandlesFetched { get; set; }

    /// <summary>Chunks the range was split into.</summary>
    public int Chunks { get; set; }

    /// <summary>Chunks skipped because every session in them already had candles.</summary>
    public int SkippedChunks { get; set; }
}
