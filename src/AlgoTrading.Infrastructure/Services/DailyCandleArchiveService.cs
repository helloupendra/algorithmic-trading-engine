using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// The nightly archive: every symbol that ticked during the day gets 1, 5 and
/// 15 minute candles built from its live 1-minute bars, and the index symbols
/// get the broker's own candles on top.
/// </summary>
/// <remarks>
/// Why this exists. Raw ticks are kept for a window (90 days) and then their
/// chunks are dropped; the live 1-minute bars are kept but nothing built the
/// 5 and 15 minute series from them, and the broker backfill only ran when a
/// person clicked it — so the candle table stopped on the last day someone
/// remembered. Options are the sharp end: the broker serves no history for
/// an expired contract, so the bars captured live are the only record of
/// what that strike did.
///
/// Ownership. Candles written here carry source "live". A row already owned
/// by another source (the broker's backfill writes "fyers") is never touched:
/// the broker's candle is the official one. A "live" row is updated when the
/// bars behind it changed — an archive run during the session followed by
/// the one at night simply completes the day.
/// </remarks>
public class DailyCandleArchiveService : IDailyCandleArchiveService
{
    public const string SourceKey = "live";
    private static readonly int[] Minutes = { 1, 5, 15 };

    // The broker's candles are worth a request for these; everything else
    // (options, futures) is archived from the live bars alone so a day's
    // archive costs a dozen broker calls, not hundreds against the quota.
    private static readonly string[] DefaultBrokerSymbols =
    {
        "NSE:NIFTY50-INDEX", "NSE:NIFTYBANK-INDEX", "BSE:SENSEX-INDEX", "NSE:FINNIFTY-INDEX",
    };

    private readonly TradingDbContext _db;
    private readonly ISymbolUniverseService _symbolUniverse;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DailyCandleArchiveService> _logger;

    public DailyCandleArchiveService(
        TradingDbContext db,
        ISymbolUniverseService symbolUniverse,
        IConfiguration configuration,
        ILogger<DailyCandleArchiveService> logger)
    {
        _db = db;
        _symbolUniverse = symbolUniverse;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CandleArchiveResult> ArchiveDayAsync(DateOnly istDay, bool includeBrokerBackfill, CancellationToken cancellationToken = default)
    {
        var result = new CandleArchiveResult { Day = istDay, RanAtUtc = DateTime.UtcNow };
        foreach (var m in Minutes)
        {
            result.CandlesInserted[m.ToString()] = 0;
            result.CandlesUpdated[m.ToString()] = 0;
        }

        var fromUtc = IstTime.StartOfDayUtc(istDay);
        var toUtc = IstTime.EndOfDayUtc(istDay);

        // The broker first, so its rows exist before the live rollup looks for owners.
        if (includeBrokerBackfill)
            await BackfillFromBrokerAsync(istDay, result, cancellationToken);

        var symbols = await _db.LiveBars.AsNoTracking()
            .Where(b => b.Resolution == "1m" && b.BarStartUtc >= fromUtc && b.BarStartUtc <= toUtc)
            .Select(b => b.Symbol)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync(cancellationToken);
        result.SymbolsWithLiveBars = symbols.Count;

        foreach (var symbol in symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var bars = await _db.LiveBars.AsNoTracking()
                    .Where(b => b.Symbol == symbol && b.Resolution == "1m" && b.BarStartUtc >= fromUtc && b.BarStartUtc <= toUtc)
                    .OrderBy(b => b.BarStartUtc)
                    .ToListAsync(cancellationToken);
                // A bar stamped exactly midnight IST is a date, not a minute: BSE
                // replays the previous close with a date-only stamp before the
                // open, and one such row per contract survives in live_bars.
                // Rolling it up would invent a 00:00 candle.
                bars = bars
                    .Where(b => !IstTime.IsMidnightIst(b.BarStartUtc))
                    // NSE/BSE pre-open (09:00–09:15 IST) is an auction, not a
                    // session: a few indicative prints that put a wick to
                    // nowhere on the first candle. The broker's own candles
                    // start at 09:15, and so do these. MCX has no pre-open.
                    .Where(b => !IsExchangePreOpen(symbol, b.BarStartUtc))
                    .ToList();
                result.LiveBarsRead += bars.Count;

                // Pre-open candles written by earlier archive runs (before
                // the rule above existed) are taken back out, so a chart of
                // that day does not open on the auction's wick.
                var stalePreOpen = await _db.Candles
                    .Where(c => c.Symbol == symbol && c.SourceKey == SourceKey && c.TimeStampUtc >= fromUtc && c.TimeStampUtc <= toUtc)
                    .ToListAsync(cancellationToken);
                stalePreOpen = stalePreOpen.Where(c => IsExchangePreOpen(symbol, c.TimeStampUtc)).ToList();
                if (stalePreOpen.Count > 0)
                {
                    _db.Candles.RemoveRange(stalePreOpen);
                    await _db.SaveChangesAsync(cancellationToken);
                }

                if (bars.Count == 0) continue;

                foreach (var m in Minutes)
                {
                    var rolled = LiveBarRollup.Roll(bars, m);
                    await WriteAsync(symbol, m.ToString(), rolled, result, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Candle archive: {Symbol} on {Day} failed.", symbol, istDay);
                result.Errors.Add($"{symbol}: {ex.Message}");
            }
        }

        _logger.LogInformation(
            "Candle archive for {Day}: {Symbols} symbols, {Bars} live bars, inserted {Inserted}, updated {Updated}, {Owned} owned by the broker, {Errors} errors.",
            istDay, result.SymbolsWithLiveBars, result.LiveBarsRead,
            result.CandlesInserted.Values.Sum(), result.CandlesUpdated.Values.Sum(), result.CandlesOwnedElsewhere, result.Errors.Count);
        return result;
    }

    private static bool IsExchangePreOpen(string symbol, DateTime barStartUtc)
    {
        if (!symbol.StartsWith("NSE:", StringComparison.Ordinal) && !symbol.StartsWith("BSE:", StringComparison.Ordinal))
            return false;
        return IstTime.ToIst(barStartUtc).TimeOfDay < IstTime.SessionOpen;
    }

    private async Task WriteAsync(string symbol, string resolution, IReadOnlyList<ProviderHistoryBar> rolled, CandleArchiveResult result, CancellationToken ct)
    {
        if (rolled.Count == 0) return;
        var stamps = rolled.Select(r => r.TimestampUtc).ToList();
        var existing = await _db.Candles
            .Where(c => c.Symbol == symbol && c.Resolution == resolution && stamps.Contains(c.TimeStampUtc))
            .ToDictionaryAsync(c => c.TimeStampUtc, ct);

        var inserted = 0;
        var updated = 0;
        foreach (var bar in rolled)
        {
            if (!existing.TryGetValue(bar.TimestampUtc, out var row))
            {
                _db.Candles.Add(new Candle
                {
                    Symbol = symbol,
                    Resolution = resolution,
                    TimeStampUtc = bar.TimestampUtc,
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = (long)bar.Volume,
                    SourceKey = SourceKey,
                });
                inserted++;
                continue;
            }

            if (!string.Equals(row.SourceKey, SourceKey, StringComparison.OrdinalIgnoreCase))
            {
                result.CandlesOwnedElsewhere++;
                continue;
            }

            var changed = row.Open != bar.Open || row.High != bar.High || row.Low != bar.Low
                          || row.Close != bar.Close || row.Volume != (long)bar.Volume;
            if (!changed) continue;
            row.Open = bar.Open;
            row.High = bar.High;
            row.Low = bar.Low;
            row.Close = bar.Close;
            row.Volume = (long)bar.Volume;
            updated++;
        }

        if (inserted > 0 || updated > 0)
            await _db.SaveChangesAsync(ct);

        result.CandlesInserted[resolution] += inserted;
        result.CandlesUpdated[resolution] += updated;
    }

    private async Task BackfillFromBrokerAsync(DateOnly istDay, CandleArchiveResult result, CancellationToken ct)
    {
        var symbols = _configuration.GetSection("Archive:BrokerSymbols").Get<string[]>();
        if (symbols is null || symbols.Length == 0) symbols = DefaultBrokerSymbols;

        foreach (var symbol in symbols)
        {
            foreach (var m in Minutes)
            {
                ct.ThrowIfCancellationRequested();
                var resolution = m.ToString();
                try
                {
                    var response = await _symbolUniverse.EnsureHistoryCoverageAsync(new BackfillHistoryRequest
                    {
                        Symbol = symbol,
                        Resolution = resolution,
                        FromDate = istDay,
                        ToDate = istDay,
                    }, ct);
                    result.BrokerBackfills.Add($"{symbol}/{resolution}: {response.CandlesFetched} candles fetched, {response.LocalCandlesAvailable} local");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Candle archive: broker backfill of {Symbol}/{Resolution} for {Day} failed.", symbol, resolution, istDay);
                    result.BrokerBackfills.Add($"{symbol}/{resolution}: failed — {ex.Message}");
                }
            }
        }
    }
}
