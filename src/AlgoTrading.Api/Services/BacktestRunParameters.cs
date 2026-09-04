// src/AlgoTrading.Api/Services/BacktestRunParameters.cs
using AlgoTrading.Contracts.Strategies;
using AlgoTrading.Infrastructure.Services;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlgoTrading.Api.Services;

/// <summary>
/// The run-level keys a backtest stores in SimulationRun.ParametersJson next to
/// the strategy's own parameters (spec §2.1): lots, the risk rules (<c>risk</c>
/// object + legacy stop_loss / target mirroring its overall level), underlying,
/// resolution (strategy form "5m"), eod_square_off_ist ("HH:MM", empty = none),
/// charges_per_lot, plus lot_size / lot_size_source — the ONE lot size
/// (current, per spec §1) the runner, the paper engine and the views all book
/// with, frozen at start so every tier agrees. Numbers may arrive as strings.
/// </summary>
public sealed record BacktestRunParameters(
    int? Lots,
    decimal? StopLoss,
    decimal? Target,
    string? Underlying,
    string? Resolution,
    string? EodSquareOffIst,
    decimal? ChargesPerLot,
    int? LotSize,
    string? LotSizeSource,
    RiskRulesDto Risk)
{
    public const string DefaultEodSquareOffIst = "15:15";
    public const string LotSizeKey = "lot_size";
    public const string LotSizeSourceKey = "lot_size_source";

    public static BacktestRunParameters Empty => new(null, null, null, null, null, null, null, null, null, RiskRulesDto.Empty());

    public static BacktestRunParameters Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Empty;
            var root = doc.RootElement;

            int? lots = ReadInt(root, "lots") ?? ReadInt(root, "quantity");
            decimal? charges = ReadDecimal(root, "charges_per_lot") ?? ReadDecimal(root, "chargesPerLot");
            int? lotSize = ReadInt(root, LotSizeKey) ?? ReadInt(root, "lotSize");
            var risk = RunRiskRules.ReadFrom(root);

            return new BacktestRunParameters(
                lots is > 0 ? lots : null,
                risk.OverallStopLoss,
                risk.OverallTarget,
                ReadUpper(root, "underlying"),
                ReadText(root, "resolution"),
                ReadText(root, "eod_square_off_ist") ?? ReadText(root, "eodSquareOffIst"),
                charges is >= 0 ? charges : null,
                lotSize is > 0 ? lotSize : null,
                ReadText(root, LotSizeSourceKey) ?? ReadText(root, "lotSizeSource"),
                risk);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>
    /// defaults ⊕ overrides ⊕ the run-level keys, as one JSON object string.
    /// <paramref name="resolution"/> may be any spelling; it is stored in the
    /// strategy form ("5m") because the runner hands it to StrategyInput.bars.
    /// </summary>
    public static string Merge(
        string? defaultsJson,
        Dictionary<string, JsonElement>? overrides,
        int lots,
        RiskRulesDto risk,
        string underlying,
        string resolution,
        string eodSquareOffIst,
        decimal chargesPerLot,
        int lotSize,
        string lotSizeSource)
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
        merged["resolution"] = ResolutionCodes.ToStrategy(resolution);
        merged["eod_square_off_ist"] = eodSquareOffIst;
        merged["charges_per_lot"] = chargesPerLot;
        merged[LotSizeKey] = Math.Max(1, lotSize);
        merged[LotSizeSourceKey] = lotSizeSource;
        RunRiskRules.WriteInto(merged, risk);

        return merged.ToJsonString();
    }

    /// <summary>
    /// Validates an "HH:MM" IST square-off time. Null/whitespace means the
    /// default; an empty string means none. Returns null with a message when invalid.
    /// </summary>
    public static string? NormalizeEodSquareOff(string? value, out string? error)
    {
        error = null;
        if (value is null) return DefaultEodSquareOffIst;

        var trimmed = value.Trim();
        if (trimmed.Length == 0) return string.Empty;

        if (!TimeOnly.TryParseExact(trimmed, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            error = "eodSquareOffIst must be \"HH:MM\" (IST) or an empty string for no square-off.";
            return null;
        }

        var span = time.ToTimeSpan();
        if (span < IstTime.SessionOpen || span > IstTime.SessionClose)
        {
            error = "eodSquareOffIst must fall inside the 09:15–15:30 IST session.";
            return null;
        }

        return time.ToString("HH:mm", CultureInfo.InvariantCulture);
    }

    private static string? ReadText(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            _ => null
        };
    }

    private static string? ReadUpper(JsonElement obj, string name)
    {
        var text = ReadText(obj, name);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim().ToUpperInvariant();
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
