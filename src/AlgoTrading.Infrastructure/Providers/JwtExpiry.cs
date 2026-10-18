using System.Text.Json;

namespace AlgoTrading.Infrastructure.Providers;

/// <summary>The end of a broker's token, read from the token itself.</summary>
public static class JwtExpiry
{
    /// <summary>
    /// The <c>exp</c> claim of a JWT, or null when the text is not one. The signature is not checked: this only
    /// decides what to show and whether sending the token is worth a call, and the broker is still the judge of
    /// whether it is valid.
    /// </summary>
    public static DateTime? ExpiresUtc(string? token)
    {
        string[] parts = (token ?? string.Empty).Split('.');
        if (parts.Length != 3) return null;
        try
        {
            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out long seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
