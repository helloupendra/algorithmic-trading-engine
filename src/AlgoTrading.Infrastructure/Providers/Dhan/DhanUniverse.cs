using System.Text.Json;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>The instruments the Dhan feed streams beyond the watchlist, and how they were chosen.</summary>
public sealed record DhanUniverse(
    IReadOnlyList<string> Symbols,
    DateTime GeneratedUtc,
    IReadOnlyDictionary<string, int> Counts,
    IReadOnlyDictionary<string, decimal?> Spots,
    IReadOnlyList<string> Warnings);

/// <summary>The last universe built, shared by every request (a singleton).</summary>
public sealed class DhanUniverseCache
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public DhanUniverse? Last { get; set; }
}

/// <summary>The selection rules, pure so tests pin them.</summary>
public static class DhanUniverseRules
{
    /// <summary>
    /// The at-the-money strike and <paramref name="eachSide"/> listed strikes on
    /// each side of it. Counted in listed strikes, not in points, so it holds for
    /// NIFTY's 50-point ladder and CRUDEOIL's alike.
    /// </summary>
    public static IReadOnlyList<decimal> NearestStrikes(IEnumerable<decimal> strikes, decimal spot, int eachSide)
    {
        var ladder = strikes.Distinct().Order().ToList();
        if (ladder.Count == 0 || spot <= 0) return Array.Empty<decimal>();

        int atm = 0;
        for (int i = 1; i < ladder.Count; i++)
        {
            if (Math.Abs(ladder[i] - spot) < Math.Abs(ladder[atm] - spot)) atm = i;
        }

        int from = Math.Max(0, atm - Math.Max(0, eachSide));
        int to = Math.Min(ladder.Count - 1, atm + Math.Max(0, eachSide));
        return ladder.GetRange(from, to - from + 1);
    }

    /// <summary>
    /// The option expiries to stream: the nearest, and on its expiry day the next
    /// one too, because that is the day positions roll into it.
    /// </summary>
    public static IReadOnlyList<DateOnly> OptionExpiries(IEnumerable<DateOnly> expiries, DateOnly today)
    {
        var ahead = expiries.Where(d => d >= today).Distinct().Order().ToList();
        if (ahead.Count == 0) return ahead;
        return ahead[0] == today ? ahead.Take(2).ToList() : ahead.Take(1).ToList();
    }
}

/// <summary>
/// Chooses what the Dhan feed streams: the indices, the nearest futures, the
/// at-the-money options of the index and MCX underlyings. It follows the market,
/// so the feed asks again every few minutes.
/// </summary>
/// <remarks>
/// Only instruments the Dhan instrument import mapped are returned: a symbol the
/// feed cannot resolve would be subscribed to nothing and look like silence.
/// The size is deliberate. Dhan sends about ten updates a second per contract,
/// and the server records every update it is sent, so the universe is the
/// contracts analysis needs, not every contract Dhan lists.
/// </remarks>
public sealed class DhanUniverseBuilder
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly DhanUniverseCache _cache;
    private readonly TradingDbContext _db;
    private readonly DhanApiClient _api;
    private readonly DhanUniverseSettings _settings;
    private readonly ILogger<DhanUniverseBuilder> _logger;

    public DhanUniverseBuilder(DhanUniverseCache cache, TradingDbContext db, DhanApiClient api, IOptions<DhanSettings> settings, ILogger<DhanUniverseBuilder> logger)
    {
        _cache = cache;
        _db = db;
        _api = api;
        _settings = settings.Value.Universe;
        _logger = logger;
    }

    /// <summary>Cached for a minute: several callers in a row cost one quote call.</summary>
    public async Task<DhanUniverse> GetAsync(CancellationToken cancellationToken = default)
    {
        await _cache.Gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache.Last is { } hit && DateTime.UtcNow - hit.GeneratedUtc < CacheFor) return hit;
            _cache.Last = await BuildAsync(cancellationToken);
            return _cache.Last;
        }
        finally
        {
            _cache.Gate.Release();
        }
    }

    private sealed record Contract(string Symbol, string Exchange, string Underlying, string Type, decimal? Strike, DateOnly Expiry, string VendorSymbol);

    private async Task<DhanUniverse> BuildAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, IstTime.Zone));
        var warnings = new List<string>();

        var indexNames = DhanSettingsLists.Split(_settings.IndexUnderlyings)
            .Where(n => DhanInstruments.IndexUnderlyings.ContainsKey(n)).ToList();
        var mcxFutures = DhanSettingsLists.Split(_settings.McxFutureUnderlyings);
        var mcxOptions = DhanSettingsLists.Split(_settings.McxOptionUnderlyings);
        var allNames = indexNames.Concat(mcxFutures).Concat(mcxOptions).Distinct().ToList();

        // Every live futures contract of these underlyings with a Dhan id: a few dozen rows.
        var futures = await MappedAsync(
            i => allNames.Contains(i.Underlying) && i.InstrumentType == "FUT" && i.ExpiryDate >= today, cancellationToken);

        var symbols = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        // 1. Indices: a fixed table, always resolvable.
        foreach (var name in indexNames)
            symbols.Add(DhanInstruments.Indices.First(kv => kv.Value == DhanInstruments.IndexUnderlyings[name]).Key);
        counts["indices"] = symbols.Count;

        // 2. The nearest futures.
        var futureSymbols = futures
            .Where(f => indexNames.Contains(f.Underlying) ? f.Exchange != "MCX" : f.Exchange == "MCX")
            .GroupBy(f => f.Underlying)
            .SelectMany(g => g.OrderBy(f => f.Expiry).Take(Math.Max(0, _settings.FuturesPerUnderlying)))
            .Select(f => f.Symbol)
            .ToList();
        symbols.AddRange(futureSymbols);
        counts["futures"] = futureSymbols.Count;

        // 3. At-the-money options around each underlying's price.
        var nearestFuture = futures.Where(f => f.Exchange == "MCX")
            .GroupBy(f => f.Underlying)
            .ToDictionary(g => g.Key, g => g.OrderBy(f => f.Expiry).First());
        var spots = await SpotsAsync(indexNames, mcxOptions, nearestFuture, warnings, cancellationToken);

        int options = 0;
        foreach (var name in indexNames.Concat(mcxOptions).Distinct())
        {
            if (spots.GetValueOrDefault(name) is not { } spot)
            {
                warnings.Add($"{name}: no price to find the at-the-money strike from; its options are left out.");
                continue;
            }

            bool mcx = !indexNames.Contains(name);
            var expiries = await _db.Instruments.AsNoTracking()
                .Where(i => i.Underlying == name && (i.OptionType == "CE" || i.OptionType == "PE") && i.ExpiryDate >= today
                            && (mcx ? i.Exchange == "MCX" : i.Exchange != "MCX"))
                .Select(i => i.ExpiryDate!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);

            foreach (var expiry in DhanUniverseRules.OptionExpiries(expiries, today))
            {
                var contracts = await MappedAsync(
                    i => i.Underlying == name && i.ExpiryDate == expiry && (i.OptionType == "CE" || i.OptionType == "PE")
                         && (mcx ? i.Exchange == "MCX" : i.Exchange != "MCX"),
                    cancellationToken);

                var chosen = DhanUniverseRules
                    .NearestStrikes(contracts.Where(c => c.Strike is not null).Select(c => c.Strike!.Value), spot, _settings.StrikesEachSide)
                    .ToHashSet();

                var picked = contracts
                    .Where(c => c.Strike is { } k && chosen.Contains(k))
                    .OrderBy(c => c.Strike).ThenBy(c => c.Type, StringComparer.Ordinal)
                    .Select(c => c.Symbol)
                    .ToList();
                symbols.AddRange(picked);
                options += picked.Count;
            }
        }
        counts["options"] = options;

        var distinct = symbols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        counts["total"] = distinct.Count;

        _logger.LogInformation(
            "Dhan universe: {Total} instruments ({Indices} indices, {Futures} futures, {Options} options){Warnings}.",
            distinct.Count, counts["indices"], counts["futures"], options,
            warnings.Count > 0 ? $"; {string.Join(" ", warnings)}" : string.Empty);

        return new DhanUniverse(distinct, now, counts, spots, warnings);
    }

    private async Task<List<Contract>> MappedAsync(
        System.Linq.Expressions.Expression<Func<Domain.Entities.Instrument, bool>> filter,
        CancellationToken cancellationToken)
    {
        var rows = await _db.Instruments.AsNoTracking()
            .Where(filter)
            .Join(_db.InstrumentVendorSymbols.AsNoTracking().Where(v => v.ProviderKey == DhanProvider.Key),
                i => i.Symbol, v => v.CanonicalSymbol,
                (i, v) => new { i.Symbol, i.Exchange, i.Underlying, i.InstrumentType, i.OptionType, i.StrikePrice, i.ExpiryDate, v.VendorSymbol })
            .ToListAsync(cancellationToken);

        return rows
            .Where(r => r.ExpiryDate is not null)
            .Select(r => new Contract(r.Symbol, r.Exchange, r.Underlying,
                string.IsNullOrEmpty(r.OptionType) ? r.InstrumentType : r.OptionType,
                r.StrikePrice, r.ExpiryDate!.Value, r.VendorSymbol))
            .ToList();
    }

    /// <summary>
    /// Each underlying's price: Dhan's own last price first (one call for all of
    /// them), then the newest recorded quote from any source, then the newest
    /// stored candle, then the spot the last chain snapshot carried. On a holiday Dhan answers NSE with nothing, and
    /// the last close is still the right centre for tomorrow's strikes.
    /// </summary>
    private async Task<Dictionary<string, decimal?>> SpotsAsync(
        IReadOnlyList<string> indexNames,
        IReadOnlyList<string> mcxOptions,
        IReadOnlyDictionary<string, Contract> nearestFuture,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var spots = new Dictionary<string, decimal?>(StringComparer.Ordinal);

        // Where each underlying's price comes from: the index itself, or for MCX the future its options are written on.
        var sources = new Dictionary<string, (string Canonical, DhanInstrument Dhan)>(StringComparer.Ordinal);
        foreach (var name in indexNames)
        {
            var index = DhanInstruments.IndexUnderlyings[name];
            sources[name] = (DhanInstruments.Indices.First(kv => kv.Value == index).Key, index);
        }
        foreach (var name in mcxOptions)
        {
            if (nearestFuture.TryGetValue(name, out var future) && DhanInstrument.Parse(future.VendorSymbol) is { } dhan)
                sources[name] = (future.Symbol, dhan);
            else
                warnings.Add($"{name}: no mapped future to price its options from.");
        }

        if (sources.Count == 0) return spots;

        try
        {
            var body = sources.Values
                .GroupBy(s => s.Dhan.Segment)
                .ToDictionary(g => g.Key, g => g.Select(s => s.Dhan.SecurityId).Distinct().ToArray());
            using var answer = await _api.PostAsync("/marketfeed/ltp", body, DhanRateClass.Quote, cancellationToken);
            var prices = ReadLastPrices(answer.RootElement);
            foreach (var (name, source) in sources)
            {
                if (prices.TryGetValue((source.Dhan.Segment, source.Dhan.SecurityId), out var price) && price > 0)
                    spots[name] = price;
            }
        }
        catch (DhanApiException ex)
        {
            warnings.Add($"Dhan's last prices were not available ({ex.Message}); recorded prices were used.");
        }

        foreach (var (name, source) in sources)
        {
            if (spots.ContainsKey(name)) continue;

            var recorded = await _db.LiveQuotesLatest.AsNoTracking()
                .Where(q => q.Symbol == source.Canonical && q.LastTradedPrice != null && q.LastTradedPrice > 0)
                .OrderByDescending(q => q.UpdatedUtc)
                .Select(q => q.LastTradedPrice)
                .FirstOrDefaultAsync(cancellationToken);

            recorded ??= await _db.Candles.AsNoTracking()
                .Where(c => c.Symbol == source.Canonical && c.Close > 0)
                .OrderByDescending(c => c.TimeStampUtc)
                .Select(c => (decimal?)c.Close)
                .FirstOrDefaultAsync(cancellationToken);

            recorded ??= await _db.OptionChainSnapshots.AsNoTracking()
                .Where(s => s.Underlying == name && s.SpotPrice > 0)
                .OrderByDescending(s => s.CapturedUtc)
                .Select(s => (decimal?)s.SpotPrice)
                .FirstOrDefaultAsync(cancellationToken);

            spots[name] = recorded;
        }

        return spots;
    }

    /// <summary>Reads {"data":{"IDX_I":{"13":{"last_price":24010.5}},"MCX_COMM":{…}},"status":"success"}.</summary>
    internal static Dictionary<(string Segment, long SecurityId), decimal> ReadLastPrices(JsonElement root)
    {
        var prices = new Dictionary<(string, long), decimal>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return prices;

        foreach (var segment in data.EnumerateObject())
        {
            if (segment.Value.ValueKind != JsonValueKind.Object) continue;
            foreach (var item in segment.Value.EnumerateObject())
            {
                if (long.TryParse(item.Name, out long id) &&
                    item.Value.ValueKind == JsonValueKind.Object &&
                    item.Value.TryGetProperty("last_price", out var lp) &&
                    lp.ValueKind == JsonValueKind.Number && lp.TryGetDecimal(out var price))
                {
                    prices[(segment.Name, id)] = price;
                }
            }
        }

        return prices;
    }
}
