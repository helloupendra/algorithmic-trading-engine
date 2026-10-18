using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.MarketData;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Portfolio;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.Portfolio;

/// <summary>
/// The desk's own prices for a portfolio: which listed instrument each ISIN is, the daily closes the desk stores
/// (fetched through its history connector when missing, and kept), and the holdings valued with them.
/// </summary>
/// <remarks>
/// <para>
/// Daily closes live in the candles table like every other history the desk keeps, under the instrument's own
/// symbol at resolution "D". Only completed sessions are fetched (<see cref="PortfolioSessions.LastCompletedSession"/>):
/// a day's bar fetched mid-session would be stored with a close that is not one.
/// </para>
/// <para>
/// A sync fetches at most <see cref="PortfolioSettings.HistoryFetchesPerSync"/> symbols, the largest holdings first,
/// and only those whose stored closes stop short of the last completed session; the history connector is the live
/// desk's too. A symbol that cannot be fetched is left as it is, and the report's risk figures say how much of the
/// value they cover.
/// </para>
/// <para>
/// Each fetch runs in a scope of its own. The history store saves through its scope's database context, and in the
/// sync's own context that save would also write the sync's unsaved changes, and a refused one (two users' syncs
/// fetching the same symbol at once) would leave its rows tracked there to fail the sync's own save after it.
/// </para>
/// </remarks>
public sealed class PortfolioPrices(
    TradingDbContext db,
    IServiceScopeFactory scopes,
    PortfolioSessions sessions,
    IOptionsMonitor<PortfolioSettings> settings,
    ILogger<PortfolioPrices> logger)
{
    /// <summary>NIFTY 50, the benchmark the beta is measured against, as the desk names it.</summary>
    public const string IndexSymbol = "NSE:NIFTY50-INDEX";

    private const string Daily = "D";

    /// <summary>The desk's symbol and name for each ISIN, from the instrument master's cash-market rows, NSE first.</summary>
    public async Task<IReadOnlyDictionary<string, (string Symbol, string Name)>> InstrumentsAsync(
        IReadOnlyCollection<string> isins, CancellationToken cancellationToken)
    {
        var wanted = isins.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<string, (string, string)>();

        var rows = await db.Instruments.AsNoTracking()
            .Where(i => wanted.Contains(i.Isin) && i.Segment == "CM")
            .Select(i => new { i.Isin, i.Symbol, i.Exchange, i.Description })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.Isin)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var best = g
                        .OrderByDescending(r => string.Equals(r.Exchange, "NSE", StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(r => r.Symbol.EndsWith("-EQ", StringComparison.OrdinalIgnoreCase))
                        .ThenBy(r => r.Symbol, StringComparer.Ordinal)
                        .First();
                    return (best.Symbol, best.Description);
                });
    }

    /// <summary>Stored daily closes for <paramref name="symbols"/>, by IST session, from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DailyClose>>> ClosesAsync(
        IReadOnlyCollection<string> symbols, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var wanted = symbols.Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<string, IReadOnlyList<DailyClose>>();

        var fromUtc = IstTime.StartOfDayUtc(from);
        var toUtc = IstTime.EndOfDayUtc(to);
        var rows = await db.Candles.AsNoTracking()
            .Where(c => wanted.Contains(c.Symbol) && c.Resolution == Daily && c.TimeStampUtc >= fromUtc && c.TimeStampUtc <= toUtc)
            .Select(c => new { c.Symbol, c.TimeStampUtc, c.Close })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.Symbol)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<DailyClose>)g
                    .GroupBy(r => IstTime.DateOf(DateTime.SpecifyKind(r.TimeStampUtc, DateTimeKind.Utc)))
                    .Select(d => new DailyClose(d.Key, d.OrderBy(r => r.TimeStampUtc).Last().Close))
                    .OrderBy(c => c.Date)
                    .ToList());
    }

    /// <summary>
    /// Fetches the completed sessions the desk does not have yet for <paramref name="symbols"/> (largest first), at most
    /// the configured number of symbols. Answers the symbols that could not be fetched.
    /// </summary>
    /// <remarks>
    /// NIFTY 50 goes first: the risk figures need a close on every one of its sessions in the window, so its stored
    /// dates are what each other symbol is checked against. A symbol is fetched from its first missing session, whether
    /// the gap is at the end (a session since the last sync), at the start (closes another page stored for a shorter
    /// span) or in the middle; the store keeps what it already has and adds the rest.
    /// </remarks>
    public async Task<IReadOnlyList<string>> RefreshAsync(IReadOnlyList<string> symbols, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        var through = sessions.LastCompletedSession(nowUtc);
        var from = through.AddDays(-Math.Max(30, s.HistoryLookbackDays));
        var ordered = symbols.Distinct().OrderBy(x => x == IndexSymbol ? 0 : 1).ToList();
        var stored = await ClosesAsync(ordered, from, through, cancellationToken);

        var failed = new List<string>();
        IReadOnlyList<DateOnly> wanted = [];
        int fetched = 0;
        foreach (string symbol in ordered)
        {
            var have = stored.TryGetValue(symbol, out var closes) ? closes : [];
            if (FirstMissing(have, symbol == IndexSymbol ? [] : wanted, from, through) is DateOnly begin)
            {
                if (fetched < Math.Max(0, s.HistoryFetchesPerSync))
                {
                    fetched++;
                    if (!await FetchAsync(symbol, begin, through, cancellationToken)) failed.Add(symbol);
                }
                else
                {
                    failed.Add(symbol);
                }
            }

            if (symbol == IndexSymbol)
            {
                var index = await ClosesAsync([IndexSymbol], from, through, cancellationToken);
                wanted = index.TryGetValue(IndexSymbol, out var nifty) ? nifty.Select(c => c.Date).ToList() : [];
            }
        }

        return failed;
    }

    /// <summary>
    /// The first session <paramref name="have"/> lacks: from the start of the window when nothing is stored or the
    /// stored span starts late, the first of <paramref name="wanted"/> (NIFTY 50's sessions) it has no close for, or the
    /// day after its newest close when that is before <paramref name="through"/>. Null when nothing is missing. Pure.
    /// </summary>
    public static DateOnly? FirstMissing(IReadOnlyList<DailyClose> have, IReadOnlyList<DateOnly> wanted, DateOnly from, DateOnly through)
    {
        if (have.Count == 0) return from;
        var dates = have.Select(c => c.Date).ToHashSet();
        var candidates = new List<DateOnly>();
        // A week of slack at the start: the window opens on a calendar day, which may be a weekend or a holiday.
        if (have.Min(c => c.Date) > from.AddDays(7)) candidates.Add(from);
        candidates.AddRange(wanted.Where(d => d >= from && d <= through && !dates.Contains(d)).Take(1));
        var newest = have.Max(c => c.Date);
        if (newest < through) candidates.Add(newest.AddDays(1));
        return candidates.Count == 0 ? null : candidates.Min();
    }

    /// <summary>One symbol's daily closes from <paramref name="start"/>, in a scope of its own; false when they could not be fetched.</summary>
    private async Task<bool> FetchAsync(string symbol, DateOnly start, DateOnly through, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var marketData = scope.ServiceProvider.GetRequiredService<IMarketDataService>();
            await marketData.SyncHistoryAsync(new SyncHistoryRequest { Symbol = symbol, Resolution = Daily, FromDate = start, ToDate = through }, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The history connector not signed in, a symbol it does not know, a duplicate written by a sync running
            // beside this one: the closes stay as they are, and the report says what they cover.
            logger.LogInformation("Portfolio history for {Symbol} from {From} was not fetched: {Error}", symbol, start, ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// The holdings of one sync with the desk's knowledge added: which instrument each ISIN is, a price from the desk's
    /// stored close where the broker gave none, and the previous session's close for the day's change.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<BrokerHolding>>> EnrichAsync(
        IReadOnlyDictionary<string, IReadOnlyList<BrokerHolding>> byBroker, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var all = byBroker.Values.SelectMany(h => h).ToList();
        var instruments = await InstrumentsAsync(all.Select(h => h.Isin).ToList(), cancellationToken);

        // The largest holdings' closes are fetched first.
        var symbols = all
            .Where(h => instruments.ContainsKey(h.Isin))
            .GroupBy(h => instruments[h.Isin].Symbol)
            .OrderByDescending(g => g.Sum(h => h.Quantity * (h.Ltp ?? h.AvgPrice)))
            .Select(g => g.Key)
            .Prepend(IndexSymbol)
            .ToList();
        await RefreshAsync(symbols, nowUtc, cancellationToken);

        var priceSession = sessions.PriceSession(nowUtc);
        var previous = sessions.PreviousSession(priceSession);
        var closes = await ClosesAsync(symbols, previous.AddDays(-10), priceSession, cancellationToken);

        return byBroker.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<BrokerHolding>)pair.Value.Select(h => Enrich(h, instruments, closes, priceSession, previous)).ToList());
    }

    /// <summary>One holding with the desk's symbol, a price where the broker gave none, and the previous close. Pure.</summary>
    public static BrokerHolding Enrich(
        BrokerHolding holding,
        IReadOnlyDictionary<string, (string Symbol, string Name)> instruments,
        IReadOnlyDictionary<string, IReadOnlyList<DailyClose>> closes,
        DateOnly priceSession,
        DateOnly previousSession)
    {
        if (!instruments.TryGetValue(holding.Isin, out var instrument))
        {
            return holding with { PriceSource = holding.Ltp is null ? PriceSources.None : PriceSources.Broker };
        }

        var series = closes.TryGetValue(instrument.Symbol, out var c) ? c : [];
        decimal? prev = series.FirstOrDefault(x => x.Date == previousSession) is { Close: > 0 } p ? p.Close : null;
        var enriched = holding with { CanonicalSymbol = instrument.Symbol, Name = instrument.Name };

        if (holding.Ltp is not null)
        {
            return enriched with { PriceSource = PriceSources.Broker, PrevClose = prev, PrevCloseDate = prev is null ? null : previousSession };
        }

        // No broker price (Dhan): the newest stored close. Its day change only when that close is the price session's
        // own, so a close two sessions old is never shown as today's move.
        var latest = series.Where(x => x.Date <= priceSession && x.Close > 0).OrderBy(x => x.Date).LastOrDefault();
        if (latest.Close <= 0) return enriched with { PriceSource = PriceSources.None };

        bool current = latest.Date == priceSession;
        return enriched with
        {
            Ltp = latest.Close,
            PriceSource = PriceSources.DeskClose,
            PriceDate = latest.Date,
            PrevClose = current ? prev : null,
            PrevCloseDate = current && prev is not null ? previousSession : null,
        };
    }
}
