using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>
/// The history side of the Dhan connector: intraday and daily bars with open
/// interest. It does not touch the database — fetching and persisting are
/// separate jobs.
/// </summary>
public class DhanMarketDataProvider : IMarketDataProvider
{
    private readonly DhanApiClient _api;
    private readonly ISymbolMapper _symbolMapper;
    private readonly ILogger<DhanMarketDataProvider> _logger;

    public DhanMarketDataProvider(DhanApiClient api, ISymbolMapper symbolMapper, ILogger<DhanMarketDataProvider> logger)
    {
        _api = api;
        _symbolMapper = symbolMapper;
        _logger = logger;
    }

    public ProviderDescriptor Descriptor => DhanProvider.Descriptor;

    public async Task<IReadOnlyList<ProviderHistoryBar>> GetHistoryAsync(
        string canonicalSymbol,
        string resolution,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken cancellationToken = default)
    {
        var instrument = await ResolveAsync(canonicalSymbol, cancellationToken);
        string? interval = DhanHistory.IntervalFor(resolution);
        bool intraday = interval is not null;

        var bars = new List<ProviderHistoryBar>();
        foreach (var (windowFrom, windowTo) in DhanHistory.Windows(fromUtc, toUtc, intraday))
        {
            object body = intraday
                ? DhanHistory.IntradayRequest(instrument, interval!, windowFrom, windowTo)
                : DhanHistory.DailyRequest(instrument, windowFrom, windowTo);

            try
            {
                using var document = await _api.PostAsync(
                    intraday ? "/charts/intraday" : "/charts/historical", body, DhanRateClass.Data, cancellationToken);
                bars.AddRange(DhanHistory.Parse(document.RootElement, windowFrom, windowTo, intraday));
            }
            catch (DhanApiException ex) when (!ex.IsAuthFailure && ex.HttpStatus is 400 or 404)
            {
                // A refusal about this instrument, not the connection: the caller
                // skips it and keeps its sweep going.
                throw new ProviderSymbolRejectedException(DhanProvider.Key, canonicalSymbol, ex.Message);
            }
        }

        // Windows meet at their edges; a bar on a boundary is kept once.
        var result = bars
            .GroupBy(b => b.TimestampUtc)
            .Select(g => g.First())
            .OrderBy(b => b.TimestampUtc)
            .ToList();

        _logger.LogInformation(
            "Dhan history {Symbol} {Resolution}: {Bars} bar(s) for {From:u} to {To:u}.",
            canonicalSymbol, resolution, result.Count, fromUtc, toUtc);

        return result;
    }

    /// <summary>
    /// Dhan's segment and security id for a canonical symbol: the fixed index
    /// table first, then the rows the instrument import wrote.
    /// </summary>
    private async Task<DhanInstrument> ResolveAsync(string canonicalSymbol, CancellationToken cancellationToken)
    {
        if (DhanInstruments.Indices.TryGetValue(canonicalSymbol, out var index)) return index;

        string mapped = await _symbolMapper.ToVendorAsync(canonicalSymbol, DhanProvider.Key, cancellationToken);

        // The mapper hands back the canonical string when it has no row, which
        // for this vendor means "no answer" rather than "the same name".
        if (DhanInstrument.Parse(mapped) is { } instrument) return instrument;

        throw new ProviderSymbolRejectedException(
            DhanProvider.Key,
            canonicalSymbol,
            "no Dhan security id is known for this instrument — run the Dhan instrument import (POST /api/Dhan/instruments/import)");
    }
}
