// src/AlgoTrading.Infrastructure/Services/RejectionDetails.cs
using System.Text.Json;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// What a refused order asked for, kept in the <c>OrderRejected</c> risk
/// event's <c>DetailsJson</c>: <c>{"side":"BUY","lots":2}</c>.
/// </summary>
/// <remarks>
/// A refused order writes no paper order (the signal and its fills roll back
/// together), so the risk event is its only trace. Until 28 Sep that event
/// kept the run, the symbol and the reason, and the side and size were only
/// in the rate-limit reason's text; Trade → Orders showed a refusal it could
/// not say the size of. Written and read here so the two cannot drift.
/// </remarks>
public static class RejectionDetails
{
    public static string ToJson(string side, int lots)
        => JsonSerializer.Serialize(new { side, lots });

    /// <summary>
    /// The side and lots of a refusal; nulls for an event written before they
    /// were recorded, or with details that are not this shape.
    /// </summary>
    public static (string? Side, int? Lots) Read(string? detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);

            string? side = doc.RootElement.TryGetProperty("side", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
            int? lots = doc.RootElement.TryGetProperty("lots", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out int n)
                ? n
                : null;
            return (string.IsNullOrWhiteSpace(side) ? null : side.ToUpperInvariant(), lots);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
