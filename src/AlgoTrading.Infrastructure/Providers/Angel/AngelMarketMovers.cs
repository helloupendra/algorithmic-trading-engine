using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Providers.Angel;

/// <summary>What one name is doing right now, as the movers screen shows it.</summary>
public sealed record MoverRow(
    string Symbol,
    string Underlying,
    long Token,
    decimal Ltp,
    decimal PriceChangePercent,
    decimal? OpenInterest = null,
    decimal? OiChangePercent = null,
    string? BuildUp = null);

/// <summary>Put-call ratio for one underlying's near expiry.</summary>
public sealed record PcrRow(string Symbol, string Underlying, decimal Pcr);

/// <summary>Everything the movers screen needs, in one answer.</summary>
public sealed record MoversSnapshot(
    DateTimeOffset AsOfUtc,
    string ExpiryType,
    IReadOnlyList<MoverRow> PriceGainers,
    IReadOnlyList<MoverRow> PriceLosers,
    IReadOnlyList<MoverRow> BuildUp,
    IReadOnlyList<PcrRow> Pcr,
    IReadOnlyList<string> Warnings);

/// <summary>
/// How a change in open interest and a change in price read together.
/// </summary>
/// <remarks>
/// The four names every Indian options desk uses. Price and OI both rising is
/// money coming in on the long side; OI rising while price falls is fresh
/// shorts; OI falling with price rising is shorts closing; both falling is
/// longs leaving. Classifying it here, once, keeps the console and any future
/// strategy from disagreeing about what a build-up is.
/// </remarks>
public static class OiBuildup
{
    public const string LongBuildUp = "Long build-up";
    public const string ShortBuildUp = "Short build-up";
    public const string ShortCovering = "Short covering";
    public const string LongUnwinding = "Long unwinding";
    public const string Flat = "Flat";

    /// <summary>Moves smaller than this (in %) are treated as no move at all.</summary>
    public const decimal Noise = 0.05m;

    public static string Classify(decimal oiChangePercent, decimal priceChangePercent)
    {
        bool oiUp = oiChangePercent > Noise, oiDown = oiChangePercent < -Noise;
        bool priceUp = priceChangePercent > Noise, priceDown = priceChangePercent < -Noise;
        if (oiUp && priceUp) return LongBuildUp;
        if (oiUp && priceDown) return ShortBuildUp;
        if (oiDown && priceUp) return ShortCovering;
        if (oiDown && priceDown) return LongUnwinding;
        return Flat;
    }
}

/// <summary>
/// Angel One's market-wide screens: top gainers and losers, the open-interest
/// build-up behind them, and the put-call ratio per underlying.
/// </summary>
/// <remarks>
/// Six SmartAPI calls make one snapshot, and SmartAPI refuses a burst, so the
/// snapshot is cached for <see cref="CacheFor"/> and every caller gets the same
/// one. A call that fails does not sink the rest: its section comes back empty
/// with a warning naming what is missing, because a screen that silently drops
/// a panel is worse than one that says why.
///
/// <para>Angel's OI lists carry no price, so the build-up table takes the
/// price change from one quote call over the same tokens.</para>
/// </remarks>
public sealed class AngelMarketMovers
{
    public static TimeSpan CacheFor { get; } = TimeSpan.FromSeconds(45);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly AngelApiClient _client;
    private readonly ILogger<AngelMarketMovers> _logger;
    private readonly TimeProvider _time;

    private static MoversSnapshot? _cached;

    public AngelMarketMovers(AngelApiClient client, ILogger<AngelMarketMovers> logger, TimeProvider? time = null)
    {
        _client = client;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async Task<MoversSnapshot> GetAsync(string expiryType, bool force, CancellationToken cancellationToken)
    {
        string expiry = string.IsNullOrWhiteSpace(expiryType) ? "NEAR" : expiryType.ToUpperInvariant();
        var cached = _cached;
        if (!force && cached is not null && cached.ExpiryType == expiry
            && _time.GetUtcNow() - cached.AsOfUtc < CacheFor)
        {
            return cached;
        }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            cached = _cached;
            if (!force && cached is not null && cached.ExpiryType == expiry
                && _time.GetUtcNow() - cached.AsOfUtc < CacheFor)
            {
                return cached;
            }

            var warnings = new List<string>();
            var priceGainers = await ListAsync("PercPriceGainers", expiry, warnings, cancellationToken);
            var priceLosers = await ListAsync("PercPriceLosers", expiry, warnings, cancellationToken);
            var oiGainers = await ListAsync("PercOIGainers", expiry, warnings, cancellationToken);
            var oiLosers = await ListAsync("PercOILosers", expiry, warnings, cancellationToken);
            var pcr = await PcrAsync(warnings, cancellationToken);

            var oiRows = oiGainers.Concat(oiLosers).ToList();
            var quotes = await QuotesAsync(oiRows.Select(r => r.Token), warnings, cancellationToken);

            var snapshot = new MoversSnapshot(
                _time.GetUtcNow(), expiry,
                priceGainers, priceLosers,
                MoversAssembly.BuildUp(oiRows, quotes),
                pcr, warnings);
            _cached = snapshot;
            return snapshot;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<IReadOnlyList<MoverRow>> ListAsync(string dataType, string expiry, List<string> warnings,
        CancellationToken cancellationToken)
    {
        var result = await _client.CallAsync("/rest/secure/angelbroking/marketData/v1/gainersLosers",
            new { datatype = dataType, expirytype = expiry }, cancellationToken);
        if (!result.Ok || result.Data is null)
        {
            warnings.Add($"{dataType}: {result.Message}");
            _logger.LogWarning("Angel movers {DataType} failed: {Message}", dataType, result.Message);
            return Array.Empty<MoverRow>();
        }
        return MoversAssembly.ParseList(result.Data.Value, dataType.Contains("OI", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<PcrRow>> PcrAsync(List<string> warnings, CancellationToken cancellationToken)
    {
        var result = await _client.GetAsync("/rest/secure/angelbroking/marketData/v1/putCallRatio", cancellationToken, signIn: true);
        if (!result.Ok || result.Data is null)
        {
            warnings.Add($"putCallRatio: {result.Message}");
            return Array.Empty<PcrRow>();
        }
        return MoversAssembly.ParsePcr(result.Data.Value);
    }

    private async Task<IReadOnlyDictionary<long, MoversAssembly.QuoteFact>> QuotesAsync(IEnumerable<long> tokens,
        List<string> warnings, CancellationToken cancellationToken)
    {
        var wanted = tokens.Distinct().Take(AngelApiClient.MaxQuoteSymbols).Select(t => t.ToString()).ToList();
        if (wanted.Count == 0) return new Dictionary<long, MoversAssembly.QuoteFact>();

        var result = await _client.QuotesAsync(new Dictionary<string, IReadOnlyList<string>> { ["NFO"] = wanted },
            "FULL", cancellationToken);
        if (!result.Ok || result.Data is null)
        {
            warnings.Add($"quotes for the build-up table: {result.Message}");
            return new Dictionary<long, MoversAssembly.QuoteFact>();
        }
        return MoversAssembly.ParseQuoteFacts(result.Data.Value);
    }
}
