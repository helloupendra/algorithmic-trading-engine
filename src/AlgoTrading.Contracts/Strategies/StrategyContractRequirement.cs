// src/AlgoTrading.Contracts/Strategies/StrategyContractRequirement.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// One option contract a strategy asks the runner (or the replay engine) to
/// resolve for it, e.g. { key: "otm_ce", optionType: "CE", moneyness: "otm",
/// steps: 2, param: "otm_offset_steps" }. The strategy reads the resolved
/// contract back under <see cref="Key"/>.
///
/// Strike resolution: the effective distance is the run parameter named by
/// <see cref="Param"/> when it is set and positive, else <see cref="Points"/>
/// when set, else <see cref="Steps"/> × the underlying's strike step (a param
/// whose name ends with <c>_points</c> is read as points, otherwise as steps).
/// CE: OTM = ATM + d, ITM = ATM − d; PE: OTM = ATM − d, ITM = ATM + d;
/// ATM ignores the distance. Reported by tools/list_strategies.py; the regex
/// fallback of the catalog reports none.
/// </summary>
public class StrategyContractRequirement
{
    /// <summary>Key the strategy reads, e.g. "atm_ce", "otm_pe", "wing_pe".</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>"CE" or "PE".</summary>
    public string OptionType { get; set; } = string.Empty;

    /// <summary>"atm", "otm" or "itm".</summary>
    public string Moneyness { get; set; } = "atm";

    /// <summary>Strikes away from ATM on the underlying's grid (0 for ATM).</summary>
    public decimal Steps { get; set; }

    /// <summary>Absolute points away from ATM; wins over <see cref="Steps"/> when set.</summary>
    public decimal? Points { get; set; }

    /// <summary>Run-parameter name that overrides the distance, when the strategy exposes one.</summary>
    public string? Param { get; set; }

    /// <summary>True when the strategy copes with the contract being unavailable.</summary>
    public bool Optional { get; set; }
}
