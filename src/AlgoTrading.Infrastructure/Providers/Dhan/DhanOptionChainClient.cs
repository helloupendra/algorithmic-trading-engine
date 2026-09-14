using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>
/// Dhan's option chain: every strike of one expiry with open interest, previous
/// open interest, volume, IV, greeks and the best bid and ask, in one call.
/// </summary>
/// <remarks>
/// Its own class rather than a method on the history provider, because it answers
/// a question the <c>IMarketDataProvider</c> seam does not ask: that interface is
/// bars, and this is a ladder of quotes. Dhan allows one chain request every three
/// seconds, which <see cref="DhanRateGate"/> enforces for the whole process.
/// </remarks>
public sealed class DhanOptionChainClient
{
    private readonly DhanApiClient _api;
    private readonly ILogger<DhanOptionChainClient> _logger;

    public DhanOptionChainClient(DhanApiClient api, ILogger<DhanOptionChainClient> logger)
    {
        _api = api;
        _logger = logger;
    }

    /// <summary>Expiries Dhan lists for an underlying, nearest first.</summary>
    public async Task<IReadOnlyList<DateOnly>> GetExpiriesAsync(string underlying, CancellationToken cancellationToken = default)
    {
        var instrument = Underlying(underlying);
        using var document = await _api.PostAsync(
            "/optionchain/expirylist",
            new { UnderlyingScrip = instrument.SecurityId, UnderlyingSeg = instrument.Segment },
            DhanRateClass.OptionChain,
            cancellationToken);

        var expiries = new List<DateOnly>();
        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (DateOnly.TryParseExact(item.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    expiries.Add(date);
            }
        }

        return expiries.Order().ToList();
    }

    /// <summary>The whole chain for one underlying and expiry.</summary>
    /// <param name="underlying">"NIFTY", "BANKNIFTY", "FINNIFTY", "MIDCPNIFTY", "SENSEX", "BANKEX".</param>
    public async Task<DhanOptionChain> GetChainAsync(string underlying, DateOnly expiry, CancellationToken cancellationToken = default)
    {
        var instrument = Underlying(underlying);
        string name = underlying.Trim().ToUpperInvariant();

        using var document = await _api.PostAsync(
            "/optionchain",
            new
            {
                UnderlyingScrip = instrument.SecurityId,
                UnderlyingSeg = instrument.Segment,
                Expiry = expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
            DhanRateClass.OptionChain,
            cancellationToken);

        var chain = DhanOptionChain.Parse(document.RootElement, name, expiry);

        _logger.LogInformation(
            "Dhan chain {Underlying} {Expiry:dd MMM}: {Rows} strike(s), spot {Spot}, peak call OI {Call}, peak put OI {Put}, PCR {Pcr}.",
            name, expiry, chain.Rows.Count, chain.UnderlyingPrice, chain.PeakCallOiStrike, chain.PeakPutOiStrike, chain.PutCallOiRatio);

        return chain;
    }

    private static DhanInstrument Underlying(string underlying)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            throw new ArgumentException("An underlying is required.", nameof(underlying));

        return DhanInstruments.IndexUnderlyings.TryGetValue(underlying.Trim(), out var instrument)
            ? instrument
            : throw new NotSupportedException(
                $"Dhan option chains are wired for index underlyings so far ({string.Join(", ", DhanInstruments.IndexUnderlyings.Keys)}); '{underlying}' is not one of them.");
    }
}
