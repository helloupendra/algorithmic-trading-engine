// src/AlgoTrading.Api/Services/LiveRunParameters.cs
using AlgoTrading.Contracts.Strategies;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlgoTrading.Api.Services;

/// <summary>
/// The run-level keys a LivePaper run stores in SimulationRun.ParametersJson
/// next to the strategy's own parameters: lots, underlying and the risk rules
/// (<c>risk</c> object + legacy <c>stop_loss</c> / <c>target</c>). Numbers may
/// arrive as strings from the trader wizard. Shared by the strategy controller
/// and the startup reconciler so a run row can always be turned back into a
/// registry entry.
/// </summary>
public sealed record LiveRunParameters(int? Lots, string? Underlying, RiskRulesDto Risk)
{
    /// <summary>Overall rupee stop-loss (legacy shorthand).</summary>
    public decimal? StopLoss => Risk.OverallStopLoss;

    /// <summary>Overall rupee target (legacy shorthand).</summary>
    public decimal? Target => Risk.OverallTarget;

    public static LiveRunParameters Empty => new(null, null, RiskRulesDto.Empty());

    public static LiveRunParameters Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Empty;
            var root = doc.RootElement;

            int? lots = ReadInt(root, "lots") ?? ReadInt(root, "quantity");
            string? underlying = null;
            if (root.TryGetProperty("underlying", out var u) && u.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(u.GetString()))
            {
                underlying = u.GetString()!.Trim().ToUpperInvariant();
            }

            return new LiveRunParameters(lots is > 0 ? lots : null, underlying, RunRiskRules.ReadFrom(root));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>
    /// defaults ⊕ overrides ⊕ { lots, underlying, risk, stop_loss, target } as one JSON object.
    /// </summary>
    public static string Merge(
        string? defaultsJson,
        Dictionary<string, JsonElement>? overrides,
        int lots,
        RiskRulesDto risk,
        string underlying)
    {
        JsonObject merged;
        try
        {
            merged = string.IsNullOrWhiteSpace(defaultsJson)
                ? new JsonObject()
                : JsonNode.Parse(defaultsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            merged = new JsonObject();
        }

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                merged[key] = value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? null
                    : JsonNode.Parse(value.GetRawText());
            }
        }

        merged["lots"] = lots;
        merged["underlying"] = underlying;
        RunRiskRules.WriteInto(merged, risk);

        return merged.ToJsonString();
    }

    private static int? ReadInt(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number when el.TryGetInt32(out var n) => n,
            JsonValueKind.Number when el.TryGetDecimal(out var d) => (int)d,
            JsonValueKind.String when int.TryParse(el.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) => s,
            _ => null
        };
    }
}
