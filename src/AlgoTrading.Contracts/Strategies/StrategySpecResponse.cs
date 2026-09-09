// src/AlgoTrading.Contracts/Strategies/StrategySpecResponse.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// A strategy's specification: docs/strategies/&lt;Name&gt;.md served raw for the
/// console to render. Served by GET /api/Strategy/{id}/spec.
/// </summary>
/// <remarks>
/// The Markdown is returned as written rather than converted, so the same
/// file reads the same on GitHub and in the console (both render GFM tables
/// and $...$ maths). A strategy without a spec is a normal 200 with
/// <see cref="HasSpec"/> false — the page then shows what the author owes,
/// which a 404 could not distinguish from an unknown strategy.
/// </remarks>
public class StrategySpecResponse
{
    /// <summary>Registry name of the strategy (the spec file is named after it).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>False when docs/strategies/&lt;Name&gt;.md does not exist yet.</summary>
    public bool HasSpec { get; set; }

    /// <summary>The spec file as written; null when <see cref="HasSpec"/> is false.</summary>
    public string? Markdown { get; set; }

    /// <summary>Repo-relative path of the spec file, e.g. "docs/strategies/ShortStraddle.md", whether or not it exists.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Last write time of the spec file (UTC); null when it does not exist.</summary>
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>
    /// The "Facts (machine-readable)" yaml block of the spec as a flat map —
    /// only simple <c>key: value</c> lines are read. Null when there is no spec
    /// or no facts block.
    /// </summary>
    public Dictionary<string, string>? Facts { get; set; }
}
