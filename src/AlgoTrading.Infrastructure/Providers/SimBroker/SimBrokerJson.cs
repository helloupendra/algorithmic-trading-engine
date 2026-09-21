using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlgoTrading.Infrastructure.Providers.SimBroker;

/// <summary>
/// How an answer from the simulated broker is read, for both the trading API
/// and the back office.
/// </summary>
/// <remarks>
/// The broker has exactly one error shape — <c>{"error":{"code":…,"message":…}}</c> —
/// and keeping the reading of it in one place is what lets every caller report
/// the broker's own words instead of a status code. A body that is neither the
/// payload nor that shape is reported with its status rather than guessed at.
/// </remarks>
internal static class SimBrokerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static async Task<SimBrokerResult<T>> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            try
            {
                var value = JsonSerializer.Deserialize<T>(text, Options);
                if (value is not null) return SimBrokerResult<T>.Ok(value);
            }
            catch (JsonException)
            {
                // Falls through, with the body itself as the message.
            }

            return SimBrokerResult<T>.Failed("BAD_RESPONSE", Describe(response, text), response.StatusCode);
        }

        try
        {
            var error = JsonSerializer.Deserialize<ErrorEnvelope>(text, Options);
            if (error?.Error is not null)
                return SimBrokerResult<T>.Failed(error.Error.Code, error.Error.Message, response.StatusCode);
        }
        catch (JsonException)
        {
        }

        return SimBrokerResult<T>.Failed(
            ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), Describe(response, text), response.StatusCode);
    }

    private static string Describe(HttpResponseMessage response, string body)
        => $"HTTP {(int)response.StatusCode}: {(body.Length > 200 ? body[..200] : body)}";

    private sealed record ErrorEnvelope([property: JsonPropertyName("error")] ErrorDetail? Error);

    private sealed record ErrorDetail(string Code, string Message);
}
