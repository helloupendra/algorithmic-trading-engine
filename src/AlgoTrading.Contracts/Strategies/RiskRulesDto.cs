// src/AlgoTrading.Contracts/Strategies/RiskRulesDto.cs
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// Risk rules of a run at three levels, shared by live paper runs and
/// backtests. Every field is optional (null = not set); set values must be
/// positive. Evaluated leg → group → overall on every sweep:
/// a leg rule closes that leg only, a group rule closes every open leg of that
/// group (the run keeps going), an overall rule flattens everything and ends
/// the run. Stored as <c>parametersJson.risk</c> (camelCase) next to the
/// legacy <c>stop_loss</c> / <c>target</c> keys, which mirror the overall level.
/// </summary>
public class RiskRulesDto
{
    /// <summary>Rupee stop-loss / target on the run's TOTAL P&amp;L (realized + unrealized).</summary>
    public OverallRiskDto? Overall { get; set; }

    /// <summary>Rupee stop-loss / target per group (one OPEN_GROUP, e.g. a straddle pair).</summary>
    public GroupRiskDto? Group { get; set; }

    /// <summary>Premium-point / percent stop-loss and target per leg, measured against its entry.</summary>
    public LegRiskDto? Leg { get; set; }

    /// <summary>True when at least one rule at any level is set.</summary>
    [JsonIgnore]
    public bool HasAnyRule
        => (Overall?.HasAnyRule ?? false) || (Group?.HasAnyRule ?? false) || (Leg?.HasAnyRule ?? false);

    /// <summary>Overall rupee stop-loss, the legacy <c>stopLoss</c> shorthand.</summary>
    [JsonIgnore]
    public decimal? OverallStopLoss => Overall?.StopLoss;

    /// <summary>Overall rupee target, the legacy <c>target</c> shorthand.</summary>
    [JsonIgnore]
    public decimal? OverallTarget => Overall?.Target;

    /// <summary>Rules with only the overall level, built from the legacy stopLoss / target fields.</summary>
    public static RiskRulesDto FromLegacy(decimal? stopLoss, decimal? target) => new()
    {
        Overall = new OverallRiskDto
        {
            StopLoss = stopLoss is > 0 ? stopLoss : null,
            Target = target is > 0 ? target : null
        },
        Group = new GroupRiskDto(),
        Leg = new LegRiskDto()
    };

    /// <summary>An empty rule set (nothing enforced), with every level present.</summary>
    public static RiskRulesDto Empty() => FromLegacy(null, null);

    /// <summary>
    /// A copy with every level present (never null) so readers can index it
    /// without null checks. Values are copied as they are; validate first.
    /// </summary>
    public static RiskRulesDto Normalize(RiskRulesDto? rules) => new()
    {
        Overall = new OverallRiskDto { StopLoss = rules?.Overall?.StopLoss, Target = rules?.Overall?.Target },
        Group = new GroupRiskDto { StopLoss = rules?.Group?.StopLoss, Target = rules?.Group?.Target },
        Leg = new LegRiskDto
        {
            StopLossPoints = rules?.Leg?.StopLossPoints,
            TargetPoints = rules?.Leg?.TargetPoints,
            StopLossPercent = rules?.Leg?.StopLossPercent,
            TargetPercent = rules?.Leg?.TargetPercent
        }
    };

    /// <summary>
    /// Every set value must be greater than zero. Returns false with a message
    /// naming the offending field; a null rule set is valid (nothing enforced).
    /// </summary>
    public static bool TryValidate(RiskRulesDto? rules, out string? error)
    {
        error = null;
        if (rules is null) return true;

        var checks = new (string Name, decimal? Value)[]
        {
            ("overall.stopLoss", rules.Overall?.StopLoss),
            ("overall.target", rules.Overall?.Target),
            ("group.stopLoss", rules.Group?.StopLoss),
            ("group.target", rules.Group?.Target),
            ("leg.stopLossPoints", rules.Leg?.StopLossPoints),
            ("leg.targetPoints", rules.Leg?.TargetPoints),
            ("leg.stopLossPercent", rules.Leg?.StopLossPercent),
            ("leg.targetPercent", rules.Leg?.TargetPercent)
        };

        foreach (var (name, value) in checks)
        {
            if (value.HasValue && value.Value <= 0)
            {
                error = $"{name} must be greater than zero, or omitted.";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Compact human summary in the same shape the web client renders
    /// (<c>describeRiskRules</c>): one comma-separated item per set rule, e.g.
    /// "overall SL ₹5,000, overall target ₹8,000, group SL ₹1,000, leg SL
    /// 20 pts / 5%". Rupees use Indian digit grouping (₹1,00,000).
    /// "no risk rules" when nothing is set.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>();

        Add(parts, "overall SL", Money(Overall?.StopLoss));
        Add(parts, "overall target", Money(Overall?.Target));
        Add(parts, "group SL", Money(Group?.StopLoss));
        Add(parts, "group target", Money(Group?.Target));
        Add(parts, "leg SL", Join(Points(Leg?.StopLossPoints), Percent(Leg?.StopLossPercent)));
        Add(parts, "leg target", Join(Points(Leg?.TargetPoints), Percent(Leg?.TargetPercent)));

        return parts.Count == 0 ? "no risk rules" : string.Join(", ", parts);
    }

    private static void Add(List<string> parts, string label, string? value)
    {
        if (value is not null) parts.Add($"{label} {value}");
    }

    private static string? Join(string? a, string? b)
        => a is null ? b : b is null ? a : $"{a} / {b}";

    private static string? Money(decimal? value)
        => value.HasValue ? "₹" + IndianGrouping(value.Value) : null;

    /// <summary>
    /// "1,00,000" / "5,000" / "1,234.5": Indian digit grouping (3, then 2s),
    /// up to two decimals, matching Intl.NumberFormat('en-IN') on the client.
    /// Done by hand so it does not depend on ICU being available on the host.
    /// </summary>
    internal static string IndianGrouping(decimal value)
    {
        var abs = Math.Abs(value);
        var text = abs.ToString("0.##", CultureInfo.InvariantCulture);
        var dot = text.IndexOf('.');
        var whole = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? string.Empty : text[dot..];

        var sb = new StringBuilder();
        if (whole.Length > 3)
        {
            var head = whole[..^3];
            var tail = whole[^3..];
            var chunks = new List<string>();
            while (head.Length > 2)
            {
                chunks.Insert(0, head[^2..]);
                head = head[..^2];
            }
            if (head.Length > 0) chunks.Insert(0, head);
            sb.Append(string.Join(",", chunks)).Append(',').Append(tail);
        }
        else
        {
            sb.Append(whole);
        }

        sb.Append(fraction);
        return (value < 0 ? "−" : string.Empty) + sb;
    }

    private static string? Points(decimal? value)
        => value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) + " pts" : null;

    private static string? Percent(decimal? value)
        => value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) + "%" : null;
}

/// <summary>Rupee thresholds on the run's total P&amp;L. Trips end the run.</summary>
public class OverallRiskDto
{
    /// <summary>End the run when total P&amp;L falls to or below minus this amount.</summary>
    public decimal? StopLoss { get; set; }

    /// <summary>End the run when total P&amp;L reaches this amount.</summary>
    public decimal? Target { get; set; }

    [JsonIgnore]
    public bool HasAnyRule => StopLoss.HasValue || Target.HasValue;
}

/// <summary>Rupee thresholds per group (realized of the group + unrealized of its open legs). Trips close that group only.</summary>
public class GroupRiskDto
{
    public decimal? StopLoss { get; set; }
    public decimal? Target { get; set; }

    [JsonIgnore]
    public bool HasAnyRule => StopLoss.HasValue || Target.HasValue;
}

/// <summary>
/// Per-leg thresholds against the leg's entry premium. Adverse move = BUY:
/// entry − ltp, SELL: ltp − entry (points); percent = adverse / entry × 100.
/// When both points and percent are set, whichever trips first wins. Trips
/// close that leg only.
/// </summary>
public class LegRiskDto
{
    public decimal? StopLossPoints { get; set; }
    public decimal? TargetPoints { get; set; }
    public decimal? StopLossPercent { get; set; }
    public decimal? TargetPercent { get; set; }

    [JsonIgnore]
    public bool HasAnyRule
        => StopLossPoints.HasValue || TargetPoints.HasValue || StopLossPercent.HasValue || TargetPercent.HasValue;
}

/// <summary>Body of PATCH /api/Strategy/runs/{runId}/risk: the full replacement rule set.</summary>
public class UpdateRunRiskRequest : RiskRulesDto
{
}

/// <summary>Response of PATCH /api/Strategy/runs/{runId}/risk.</summary>
public class UpdateRunRiskResponse
{
    public long RunId { get; set; }
    public RiskRulesDto Risk { get; set; } = RiskRulesDto.Empty();
}
