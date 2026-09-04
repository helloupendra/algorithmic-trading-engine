// src/AlgoTrading.Api/Services/RunRiskRules.cs
using AlgoTrading.Contracts.Strategies;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AlgoTrading.Api.Services;

/// <summary>
/// How a run's risk rules are persisted in <c>SimulationRun.ParametersJson</c>
/// and read back, shared by live runs and backtests: the <c>risk</c> object
/// (camelCase, unset values omitted) is authoritative; the legacy
/// <c>stop_loss</c> / <c>target</c> keys always mirror the overall level so
/// older readers (and the Python runner's [CONFIG] line) keep working.
/// </summary>
public static class RunRiskRules
{
    public const string RiskKey = "risk";
    public const string StopLossKey = "stop_loss";
    public const string TargetKey = "target";

    /// <summary>Signal appended when the rules of a running run are changed.</summary>
    public const string RiskUpdatedSignalType = "RISK_UPDATED";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// The rules a start request asks for: <paramref name="requested"/> when
    /// given (its overall level falls back to the legacy fields where unset),
    /// else the overall level built from the legacy stopLoss / target fields.
    /// Non-positive values are dropped; validate before calling.
    /// </summary>
    public static RiskRulesDto Resolve(RiskRulesDto? requested, decimal? legacyStopLoss, decimal? legacyTarget)
    {
        if (requested is null)
        {
            return RiskRulesDto.FromLegacy(legacyStopLoss, legacyTarget);
        }

        var rules = RiskRulesDto.Normalize(requested);
        rules.Overall!.StopLoss ??= legacyStopLoss;
        rules.Overall!.Target ??= legacyTarget;
        return Sanitize(rules);
    }

    /// <summary>Reads the rules out of a parametersJson string (prefers <c>risk</c>, falls back to the legacy keys).</summary>
    public static RiskRulesDto Parse(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson)) return RiskRulesDto.Empty();

        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return RiskRulesDto.Empty();
            return ReadFrom(doc.RootElement);
        }
        catch (JsonException)
        {
            return RiskRulesDto.Empty();
        }
    }

    /// <summary>Reads the rules out of an already parsed parameters object.</summary>
    public static RiskRulesDto ReadFrom(JsonElement root)
    {
        decimal? legacySl = ReadDecimal(root, StopLossKey) ?? ReadDecimal(root, "stopLoss");
        decimal? legacyTarget = ReadDecimal(root, TargetKey);

        if (root.TryGetProperty(RiskKey, out var riskEl) && riskEl.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var parsed = riskEl.Deserialize<RiskRulesDto>(JsonOptions);
                if (parsed is not null)
                {
                    var rules = RiskRulesDto.Normalize(parsed);
                    // An older row written before the overall level existed in
                    // the object still carries its rupee thresholds in the legacy keys.
                    rules.Overall!.StopLoss ??= legacySl;
                    rules.Overall!.Target ??= legacyTarget;
                    return Sanitize(rules);
                }
            }
            catch (JsonException)
            {
                // Malformed risk object: the legacy keys are still authoritative below.
            }
        }

        return RiskRulesDto.FromLegacy(legacySl, legacyTarget);
    }

    /// <summary>Writes <c>risk</c> plus the legacy overall keys into a parameters object.</summary>
    public static void WriteInto(JsonObject parameters, RiskRulesDto rules)
    {
        var clean = Sanitize(RiskRulesDto.Normalize(rules));
        parameters[RiskKey] = ToJsonNode(clean);
        parameters[StopLossKey] = clean.OverallStopLoss.HasValue ? JsonValue.Create(clean.OverallStopLoss.Value) : null;
        parameters[TargetKey] = clean.OverallTarget.HasValue ? JsonValue.Create(clean.OverallTarget.Value) : null;
    }

    /// <summary>Returns <paramref name="parametersJson"/> with its risk rules replaced (other keys untouched).</summary>
    public static string Rewrite(string? parametersJson, RiskRulesDto rules)
    {
        JsonObject parameters;
        try
        {
            parameters = string.IsNullOrWhiteSpace(parametersJson)
                ? new JsonObject()
                : JsonNode.Parse(parametersJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            parameters = new JsonObject();
        }

        WriteInto(parameters, rules);
        return parameters.ToJsonString();
    }

    /// <summary>The rules as a JSON node (camelCase, unset values omitted, empty levels omitted).</summary>
    public static JsonNode ToJsonNode(RiskRulesDto rules)
    {
        var node = new JsonObject();
        if (rules.Overall is { HasAnyRule: true } overall)
        {
            node["overall"] = JsonSerializer.SerializeToNode(overall, JsonOptions);
        }
        if (rules.Group is { HasAnyRule: true } group)
        {
            node["group"] = JsonSerializer.SerializeToNode(group, JsonOptions);
        }
        if (rules.Leg is { HasAnyRule: true } leg)
        {
            node["leg"] = JsonSerializer.SerializeToNode(leg, JsonOptions);
        }
        return node;
    }

    /// <summary>MetadataJson of a RISK_UPDATED signal: { risk, by }.</summary>
    public static string UpdatedMetadata(RiskRulesDto rules, string by)
    {
        var node = new JsonObject
        {
            ["risk"] = ToJsonNode(rules),
            ["by"] = by
        };
        return node.ToJsonString();
    }

    /// <summary>
    /// Activity text of a RISK_UPDATED signal from its { risk, by } metadata:
    /// "Risk rules updated by admin: overall SL ₹5,000, leg SL 20 pts" — the
    /// fallback for clients that do not render the row from the metadata.
    /// </summary>
    public static string DescribeUpdate(string? metadataJson)
    {
        var by = SignalMetadata.ReadString(metadataJson, "by");
        var rules = Parse(metadataJson);
        var who = string.IsNullOrWhiteSpace(by) ? string.Empty : $" by {by}";
        return $"Risk rules updated{who}: {rules.Describe()}";
    }

    /// <summary>Drops non-positive values (they mean "not set" to every reader).</summary>
    public static RiskRulesDto Sanitize(RiskRulesDto rules)
    {
        var r = RiskRulesDto.Normalize(rules);
        r.Overall!.StopLoss = Positive(r.Overall.StopLoss);
        r.Overall.Target = Positive(r.Overall.Target);
        r.Group!.StopLoss = Positive(r.Group.StopLoss);
        r.Group.Target = Positive(r.Group.Target);
        r.Leg!.StopLossPoints = Positive(r.Leg.StopLossPoints);
        r.Leg.TargetPoints = Positive(r.Leg.TargetPoints);
        r.Leg.StopLossPercent = Positive(r.Leg.StopLossPercent);
        r.Leg.TargetPercent = Positive(r.Leg.TargetPercent);
        return r;
    }

    private static decimal? Positive(decimal? value) => value is > 0 ? value : null;

    private static decimal? ReadDecimal(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetDecimal(out var d) => d,
            JsonValueKind.String when decimal.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) => s,
            _ => null
        };
    }
}
