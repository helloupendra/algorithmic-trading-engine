// src/AlgoTrading.Contracts/Backtest/StartBacktestRequest.cs
using System.Text.Json;

namespace AlgoTrading.Contracts.Backtest;

/// <summary>
/// Body of POST /api/Backtest/runs: one catalog strategy replayed over one
/// underlying at one bar resolution across an IST date range.
/// </summary>
public class StartBacktestRequest
{
    /// <summary>Catalog strategy id (StrategyCatalogService.StableId). Required.</summary>
    public int StrategyId { get; set; }

    /// <summary>F&amp;O underlying, e.g. "BANKNIFTY". Required.</summary>
    public string Underlying { get; set; } = string.Empty;

    /// <summary>Canonical candle resolution: "1" | "5" | "15" | "D". Required.</summary>
    public string Resolution { get; set; } = string.Empty;

    /// <summary>First IST calendar day, "yyyy-MM-dd". Required.</summary>
    public string FromDate { get; set; } = string.Empty;

    /// <summary>Last IST calendar day, "yyyy-MM-dd" (≥ fromDate, ≤ today). Required.</summary>
    public string ToDate { get; set; } = string.Empty;

    /// <summary>Lots per leg. Defaults to the catalog's defaultLots. Must be ≥ 1.</summary>
    public int? Lots { get; set; }

    /// <summary>Stop the backtest when total P&amp;L falls to or below minus this rupee amount.</summary>
    public decimal? StopLoss { get; set; }

    /// <summary>Stop the backtest when total P&amp;L reaches this rupee amount.</summary>
    public decimal? Target { get; set; }

    /// <summary>
    /// Risk rules at three levels (overall / group / leg), enforced by the
    /// backtest engine. When absent, the overall level is built from
    /// <see cref="StopLoss"/> and <see cref="Target"/>.
    /// </summary>
    public AlgoTrading.Contracts.Strategies.RiskRulesDto? Risk { get; set; }

    /// <summary>End-of-day square-off time "HH:MM" IST. Default "15:15"; empty string = none.</summary>
    public string? EodSquareOffIst { get; set; }

    /// <summary>Flat rupees per lot per fill. Default 0.</summary>
    public decimal? ChargesPerLot { get; set; }

    /// <summary>Overrides merged over the strategy's default parameters.</summary>
    public Dictionary<string, JsonElement>? Parameters { get; set; }

    /// <summary>Paper capital for the run. Defaults to 10,00,000.</summary>
    public decimal? InitialCapital { get; set; }
}
