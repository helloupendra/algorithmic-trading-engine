using System.Globalization;
using AlgoTrading.Api.Security;
using AlgoTrading.Infrastructure.Providers.TrueData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The two things about TrueData that are its own rather than every vendor's:
/// importing the symbol master, and reading an option chain with greeks.
/// </summary>
/// <remarks>
/// Everything a connector has in common with the others — credentials, session,
/// capability matrix, routing — is on the Connectors page already and is not
/// repeated here. Its live feed is not here either: that is started and stopped
/// through FeedsController, like every other vendor's. This controller only
/// carries what neither of those can know how to ask for.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/[controller]")]
public class TrueDataController : ControllerBase
{
    private readonly TrueDataSymbolImporter _importer;
    private readonly TrueDataChainClient _chain;

    public TrueDataController(
        TrueDataSymbolImporter importer,
        TrueDataChainClient chain)
    {
        _importer = importer;
        _chain = chain;
    }

    /// <summary>
    /// Downloads TrueData's symbol masters and records its name for every
    /// derivative, so futures and monthly options can be asked for at all.
    /// </summary>
    /// <param name="segments">"fo", "mcx", "bsefo"; all three when omitted.</param>
    /// <param name="search">Narrows the download, e.g. "NIFTY". Useful for a test.</param>
    [HttpPost("symbols/import")]
    public async Task<IActionResult> ImportSymbols(
        [FromQuery] string? segments,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var list = string.IsNullOrWhiteSpace(segments)
            ? null
            : segments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = await _importer.ImportAsync(list, search, cancellationToken);

        return Ok(new
        {
            segments = results,
            mapped = results.Sum(x => x.Mapped),
            rowsRead = results.Sum(x => x.RowsRead),
            failed = results.Where(x => x.Error is not null).Select(x => x.Segment).ToArray(),
        });
    }

    /// <summary>
    /// One underlying's option chain with open interest, volume and greeks.
    /// </summary>
    /// <param name="underlying">"NIFTY", "BANKNIFTY", "CRUDEOIL", "RELIANCE".</param>
    /// <param name="expiry">yyyy-MM-dd.</param>
    [HttpGet("chain")]
    public async Task<IActionResult> GetChain(
        [FromQuery] string underlying,
        [FromQuery] string expiry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(underlying))
            return BadRequest(new { message = "underlying is required." });

        if (!DateOnly.TryParseExact(expiry, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var expiryDate))
        {
            return BadRequest(new { message = "expiry must be yyyy-MM-dd." });
        }

        var chain = await _chain.GetChainAsync(underlying, expiryDate, cancellationToken);

        return Ok(new
        {
            underlying = chain.Underlying,
            expiry = chain.Expiry.ToString("yyyy-MM-dd"),
            strikes = chain.Rows.Count,
            // The three numbers the OI rules actually read, computed once here
            // rather than in every caller.
            peakCallOiStrike = chain.PeakCallOiStrike,
            peakPutOiStrike = chain.PeakPutOiStrike,
            putCallOiRatio = chain.PutCallOiRatio,
            rows = chain.Rows.Select(r => new
            {
                strike = r.Strike,
                call = Side(r.Call),
                put = Side(r.Put),
            }),
        });
    }

    private static object Side(TrueDataChainSide s) => new
    {
        lastPrice = s.LastPrice,
        previousClose = s.PreviousClose,
        bid = s.Bid,
        bidQuantity = s.BidQuantity,
        ask = s.Ask,
        askQuantity = s.AskQuantity,
        openInterest = s.OpenInterest,
        previousOpenInterest = s.PreviousOpenInterest,
        openInterestChange = s.OpenInterestChange,
        volume = s.Volume,
        timestampUtc = s.TimestampUtc,
        greeks = s.Greeks is null ? null : new
        {
            delta = s.Greeks.Delta,
            theta = s.Greeks.Theta,
            vega = s.Greeks.Vega,
            gamma = s.Greeks.Gamma,
            rho = s.Greeks.Rho,
            impliedVolatility = s.Greeks.ImpliedVolatility,
        },
    };
}
