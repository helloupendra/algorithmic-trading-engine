// src/AlgoTrading.Contracts/Strategies/FnoUnderlyingResponse.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// An underlying with at least one unexpired option contract in the instrument
/// master, with what the launch dialog needs to show before the user picks it.
/// Served by GET /api/Instruments/derivatives/underlyings.
/// </summary>
public class FnoUnderlyingResponse
{
    public string Underlying { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;
    public string SpotSymbol { get; set; } = string.Empty;
    public int LotSize { get; set; }

    /// <summary>"master" | "configured" | "unknown"</summary>
    public string LotSizeSource { get; set; } = string.Empty;

    public decimal StrikeStep { get; set; }

    /// <summary>yyyy-MM-dd of the nearest future expiry.</summary>
    public string NextExpiry { get; set; } = string.Empty;

    /// <summary>Future expiries only, at most 8, yyyy-MM-dd.</summary>
    public List<string> Expiries { get; set; } = new();

    /// <summary>Option contracts across future expiries.</summary>
    public int OptionContracts { get; set; }
}
