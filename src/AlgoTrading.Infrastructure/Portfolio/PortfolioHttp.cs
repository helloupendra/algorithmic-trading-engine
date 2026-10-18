using System.Globalization;
using System.Text.Json;

namespace AlgoTrading.Infrastructure.Portfolio;

/// <summary>One answer from a broker: the HTTP status and the body as text.</summary>
internal readonly record struct BrokerAnswer(int Status, string Body);

/// <summary>The plumbing every portfolio client shares: one send, and lenient JSON reading.</summary>
/// <remarks>
/// A transport failure is reported by its type only: an exception's own text can carry the request, and the request
/// carries the token. The HTTP clients these send through are registered without logging
/// (<see cref="PortfolioRegistration"/>), so neither a URL nor a header reaches api.log.
/// </remarks>
internal static class PortfolioHttp
{
    public static async Task<BrokerAnswer> SendAsync(HttpClient client, HttpRequestMessage request, string brokerName, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new BrokerAnswer((int)response.StatusCode, body);
        }
        catch (HttpRequestException ex)
        {
            throw new PortfolioBrokerException($"{brokerName} could not be reached ({ex.GetType().Name}). Try again in a minute.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PortfolioBrokerException($"{brokerName} did not answer in time. Try again in a minute.");
        }
    }

    /// <summary>The body as JSON, or null when it is not JSON (an HTML error page, a plain-text refusal).</summary>
    public static JsonDocument? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Reading broker JSON whose numbers sometimes arrive as strings ("4663.4100") and whose fields come and go.</summary>
internal static class PortfolioJson
{
    public static decimal? Dec(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDecimal(out decimal d) ? d : null,
            JsonValueKind.String => decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal s) ? s : null,
            _ => null,
        };
    }

    public static string Str(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v)) return string.Empty;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => v.GetRawText(),
            _ => string.Empty,
        };
    }

    /// <summary>The objects in the array at <paramref name="name"/>; none when it is missing, null or not an array.</summary>
    public static IEnumerable<JsonElement> Objects(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        return Objects(v);
    }

    public static IEnumerable<JsonElement> Objects(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList() : [];

    /// <summary>Text cut to <paramref name="max"/> characters, for a broker's own words in a message.</summary>
    public static string Clip(string? text, int max = 160)
    {
        string t = (text ?? string.Empty).Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }
}
