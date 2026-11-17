// src/AlgoTrading.Infrastructure/Services/RunCarrySetting.cs
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// "Carry every position of this run": a run-level setting that ticks carry
/// forward on each position the run opens, as it opens.
/// </summary>
/// <remarks>
/// The owner, 17 Nov 2026: "agr trade carry hoga toh yeh apne ap tick ho jana
/// chaiye" (if the trade is to be carried, the tick should come on by itself),
/// and asked for it as a run setting ("Run ki setting se"). It lives in the
/// run's ParametersJson as <c>"carry_forward_all": true</c>, next to the risk
/// rules, so a run started with it in its parameters has it from its first
/// fill. Switching it on or off while the run trades also sets the run's open
/// positions to match (<see cref="PaperTradingService.SetRunCarryForwardAsync"/>).
/// One position's tick can still be changed by hand afterwards. A replay or a
/// recap never carries: their fills are an earlier session's prices.
/// </remarks>
public static class RunCarrySetting
{
    public const string Key = "carry_forward_all";

    /// <summary>True when the parameters say <c>"carry_forward_all": true</c> (or "true").</summary>
    public static bool IsOn(string? parametersJson)
    {
        if (string.IsNullOrWhiteSpace(parametersJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(parametersJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(Key, out var value)) return false;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.String => bool.TryParse(value.GetString(), out var b) && b,
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="parametersJson"/> with the setting on (the key set to
    /// true) or off (the key removed); every other key is left as it was.
    /// </summary>
    public static string Rewrite(string? parametersJson, bool on)
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

        if (on) parameters[Key] = true;
        else parameters.Remove(Key);
        return parameters.ToJsonString();
    }

    /// <summary>
    /// Whether a run's new positions open ticked: the setting is on and the
    /// run trades the live market (not an offline replay, not a recap).
    /// </summary>
    public static bool TicksNewPositions(string? mode, string? parametersJson) =>
        !PaperTradingService.IsReplay(mode)
        && !RecapClock.IsRecap(parametersJson)
        && IsOn(parametersJson);
}
