using System.Globalization;
using System.Text.Json.Nodes;
using AlgoTrading.Infrastructure.Ai;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// The model's answer read as a plan (<see cref="AiTraderPlan"/>), or why it could not be. The shape is checked
/// here; whether the plan is allowed is <see cref="AiTraderGuard"/>'s question.
/// </summary>
public static class AiTraderPlanReader
{
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        AiTraderPlan.None, AiTraderPlan.Buy, AiTraderPlan.Exit, AiTraderPlan.StartStrategy, AiTraderPlan.StopStrategy,
    };

    public static (AiTraderPlan? Plan, string? Error) Read(string? answer)
    {
        if (AiJson.Object(answer) is not JsonObject obj) return (null, "The answer was not the JSON object asked for.");

        string action = (AiJson.Str(obj, "action") ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_');
        if (!Actions.Contains(action)) return (null, $"action \"{action}\" is not none, buy, exit, start_strategy or stop_strategy.");

        string reason = AiJson.Str(obj, "reason")?.Trim() ?? string.Empty;
        if (reason.Length == 0) return (null, "Every decision needs its reason: the facts from the brief it rests on.");

        double? confidence = Number(obj["confidence"]);
        if (confidence is < 0 or > 1) return (null, "confidence is a number from 0 to 1.");

        return (new AiTraderPlan(
            action,
            AiJson.Str(obj, "underlying")?.Trim().ToUpperInvariant(),
            AiJson.Str(obj, "option")?.Trim().ToUpperInvariant(),
            Text(obj["strike"])?.Trim().ToUpperInvariant().Replace(" ", string.Empty),
            Number(obj["lots"]) is double lots && lots == Math.Floor(lots) ? (int)lots : null,
            Money(obj["stopLoss"] ?? obj["stop_loss"]),
            Money(obj["target"]),
            AiJson.Str(obj, "strategy")?.Trim(),
            Number(obj["positionId"] ?? obj["position_id"]) is double position ? (long)position : null,
            Number(obj["runId"] ?? obj["run_id"]) is double run ? (long)run : null,
            reason.Length <= 600 ? reason : reason[..599] + "…",
            confidence), null);
    }

    /// <summary>
    /// The strike a plan names, on the underlying's grid around the at-the-money strike: "ATM", "ATM+1" (one step
    /// up), "ATM-2", or a number. Null when it is none of those.
    /// </summary>
    public static decimal? StrikeOf(string? strike, decimal atm, decimal step)
    {
        if (string.IsNullOrWhiteSpace(strike)) return atm;
        var s = strike.Trim().ToUpperInvariant().Replace(" ", string.Empty);
        if (s == "ATM") return atm;
        if (s.StartsWith("ATM", StringComparison.Ordinal) && int.TryParse(s[3..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int steps)
            && Math.Abs(steps) <= 10)
        {
            return atm + steps * step;
        }

        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    }

    private static string? Text(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue(out string? s) => s,
        JsonValue v when v.TryGetValue(out double d) => d.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static double? Number(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue(out double d) => d,
        JsonValue v when v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    private static decimal? Money(JsonNode? node) => Number(node) is double d && d > 0 ? Math.Round((decimal)d, 2) : null;
}
