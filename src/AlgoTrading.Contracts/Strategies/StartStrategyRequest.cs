// src/AlgoTrading.Contracts/Strategies/StartStrategyRequest.cs
using System.Text.Json;

namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// Body of POST /api/Strategy/{id}/start. The underlying is mandatory; stop-loss
/// and target are optional rupee amounts on the run's total P&amp;L.
/// </summary>
public class StartStrategyRequest
{
    /// <summary>F&amp;O underlying the strategy trades, e.g. "BANKNIFTY". Required.</summary>
    public string Underlying { get; set; } = string.Empty;

    /// <summary>Lots per leg. Defaults to the catalog's defaultLots (or 1). Must be at least 1.</summary>
    public int? Lots { get; set; }

    /// <summary>Stop when total P&amp;L falls to or below minus this amount. Positive rupees or null.</summary>
    public decimal? StopLoss { get; set; }

    /// <summary>Stop when total P&amp;L reaches this amount. Positive rupees or null.</summary>
    public decimal? Target { get; set; }

    /// <summary>
    /// Risk rules at three levels (overall / group / leg). When absent, the
    /// overall level is built from <see cref="StopLoss"/> and <see cref="Target"/>.
    /// When present, its overall level wins over those legacy fields.
    /// </summary>
    public RiskRulesDto? Risk { get; set; }

    /// <summary>Overrides merged over the strategy's default parameters.</summary>
    public Dictionary<string, JsonElement>? Parameters { get; set; }

    /// <summary>Paper capital for the run. Defaults to 10,00,000.</summary>
    public decimal? InitialCapital { get; set; }
}
