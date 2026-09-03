// src/AlgoTrading.Api/Services/SignalMetadata.cs
using System.Text.Json;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Reads the free-form <c>metadataJson</c> a signal carries. Shared by the live
/// view and the backtest view so both render the same reason text.
/// </summary>
public static class SignalMetadata
{
    /// <summary>metadataJson.reason (any casing), or null.</summary>
    public static string? ReadReason(string? metadataJson)
        => ReadString(metadataJson, "reason");

    /// <summary>A top-level string property (any casing); numbers/booleans come back as their raw text.</summary>
    public static string? ReadString(string? metadataJson, string property)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(prop.Name, property, StringComparison.OrdinalIgnoreCase)) continue;
                return prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => prop.Value.GetRawText()
                };
            }
        }
        catch (JsonException)
        {
            // Free-form metadata from an older runner; fall through.
        }
        return null;
    }
}
