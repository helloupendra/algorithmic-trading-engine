using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.TrueData;

/// <summary>
/// TrueData's option chain, with open interest, volume and greeks in one call.
/// </summary>
/// <remarks>
/// Worth its own class rather than another method on the history provider,
/// because it answers a question the <c>IMarketDataProvider</c> seam does not
/// ask: that interface is bars, and this is a ladder of live quotes.
///
/// <para>One request returns both sides of every strike — last price, previous
/// close, best bid and ask with their sizes, open interest and the previous
/// day's, volume, and delta/theta/vega/gamma/rho/IV. The platform prices its own
/// greeks from the chain today because FYERS does not send them; where this is
/// available they can be compared against a vendor that does.</para>
/// </remarks>
public sealed class TrueDataChainClient
{
    private readonly TrueDataTokenStore _tokens;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TrueDataSettings _settings;
    private readonly ILogger<TrueDataChainClient> _logger;

    public TrueDataChainClient(
        IOptions<TrueDataSettings> settings,
        TrueDataTokenStore tokens,
        IHttpClientFactory httpClientFactory,
        ILogger<TrueDataChainClient> logger)
    {
        _settings = settings.Value;
        _tokens = tokens;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// The whole chain for one underlying and expiry.
    /// </summary>
    /// <param name="underlying">"NIFTY", "BANKNIFTY", "RELIANCE", "CRUDEOIL".</param>
    public async Task<TrueDataOptionChain> GetChainAsync(
        string underlying,
        DateOnly expiry,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            throw new ArgumentException("An underlying is required.", nameof(underlying));

        string url = QueryHelpers.AddQueryString(
            $"{_settings.GreeksBaseUrl.TrimEnd('/')}/api/getOptionChainwithGreeks",
            new Dictionary<string, string?>
            {
                ["symbol"] = underlying.Trim().ToUpperInvariant(),
                // TrueData spells an expiry dd-MM-yyyy on this host, and yyMMdd
                // on the history one. Neither is negotiable, so each is written
                // where it is needed rather than normalised into one wrong shape.
                ["expiry"] = expiry.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture),
                ["response"] = "csv",
            });

        string csv = await SendAsync(url, cancellationToken);
        var chain = TrueDataChainCsv.Parse(csv, underlying.Trim().ToUpperInvariant(), expiry);

        _logger.LogInformation(
            "TrueData chain {Underlying} {Expiry:dd MMM}: {Rows} strike(s), peak call OI {Call}, peak put OI {Put}, PCR {Pcr}.",
            underlying, expiry, chain.Rows.Count, chain.PeakCallOiStrike, chain.PeakPutOiStrike, chain.PutCallOiRatio);

        return chain;
    }

    private async Task<string> SendAsync(string url, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient(TrueDataProvider.Key);

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            string token = await _tokens.GetTokenAsync(cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));

            using var response = await http.SendAsync(request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
            {
                _tokens.Invalidate();
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"TrueData option chain failed ({(int)response.StatusCode}): " +
                    (body.Length > 200 ? body[..200] : body));
            }

            return body;
        }

        throw new InvalidOperationException("TrueData rejected the token twice in a row.");
    }
}
