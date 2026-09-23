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

    /// <summary>
    /// Start the run in another trader's account. Admins only; anyone else is
    /// refused rather than quietly ignored.
    /// </summary>
    /// <remarks>
    /// This is how the morning job deploys the same strategies into every
    /// trading account without holding anyone's password. The run belongs to
    /// this trader — their grants decide whether it may start at all, their
    /// console shows it, and their P&amp;L carries it — while the audit trail
    /// still records the admin who asked for it.
    /// </remarks>
    public long? OwnerUserId { get; set; }
}
