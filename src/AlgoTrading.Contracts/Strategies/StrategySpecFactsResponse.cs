// src/AlgoTrading.Contracts/Strategies/StrategySpecFactsResponse.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// One strategy's spec facts without the document — a row of
/// GET /api/Strategy/specs/facts. The Library page reads every strategy's
/// facts (resolution, data, built-in exit) for its cards and filters; the
/// full Markdown of all specs together is over half a megabyte, so the list
/// carries the parsed block only.
/// </summary>
public class StrategySpecFactsResponse
{
    /// <summary>Catalog id of the strategy (the same id GET /api/Strategy returns).</summary>
    public int Id { get; set; }

    /// <summary>Registry name of the strategy.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>False when docs/strategies/&lt;Name&gt;.md does not exist yet.</summary>
    public bool HasSpec { get; set; }

    /// <summary>The parsed facts block, as in <see cref="StrategySpecResponse.Facts"/>; null without a spec or a block.</summary>
    public Dictionary<string, string>? Facts { get; set; }
}
