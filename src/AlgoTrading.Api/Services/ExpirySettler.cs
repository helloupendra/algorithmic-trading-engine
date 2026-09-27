// src/AlgoTrading.Api/Services/ExpirySettler.cs
using System.Globalization;
using System.Text.Json;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Settles the manual book's positions in contracts that have expired.
/// </summary>
/// <remarks>
/// <para>
/// Hand-placed positions are carried across days (27 Sep: "if I want to carry
/// forward a position, it should carry"), and a carried position eventually
/// meets its expiry. Nothing settled one: the contract stopped quoting, the
/// row kept its last mark, and an expired option sat "open" in the book for
/// ever, its P&amp;L frozen at whatever it last traded at — neither what it
/// was worth at expiry nor zero.
/// </para>
/// <para>
/// After the expiry day's close (plus <see cref="Grace"/>), each such position
/// is closed at what the exchange would settle it at:
/// </para>
/// <list type="bullet">
/// <item>an option at its intrinsic value against the underlying's closing
/// price — CE max(0, S − K), PE max(0, K − S), so an out-of-the-money leg
/// settles at 0. Index options are cash-settled on the index close, which is
/// exactly this. MCX options are written on a future and in reality devolve
/// into a futures position; here they are cash-settled against that future's
/// close the same way. That is a paper simplification, and deliberate: the
/// book holds no future the operator did not buy. Stock options are
/// physically settled in reality and are cash-settled at intrinsic here for
/// the same reason.</item>
/// <item>a future at its own last price.</item>
/// </list>
/// <para>
/// S is the underlying's last price of the expiry day, observed within the
/// last <see cref="NearTheClose"/> of its session: the last one-minute bar the
/// platform recorded, else a stored intraday candle, else the live quote if it
/// last moved that day, else the day's stored daily candle. An index's
/// official close is computed from its constituents and can differ from its
/// last print by a few points; the reason line names the price used so the
/// difference is visible, not hidden. With no price at all, nothing is
/// invented: the position stays open, one warning is logged, and the next
/// pass tries again.
/// </para>
/// <para>
/// Idempotent: a position already closed is skipped by the write itself
/// (<see cref="IPaperTradingService.SettleExpiredPositionAsync"/>), so a pass
/// after a restart or a second pass in the same minute changes nothing. The
/// same pass is the start-up catch-up: anything that expired while the API was
/// down is due the moment it next runs, and it is stamped at the expiry's
/// close, not at the moment the API came back.
/// </para>
/// </remarks>
public sealed class ExpirySettler
{
    /// <summary>How long after the close a contract is settled: 15:35 IST for NSE and BSE.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    /// <summary>How recent a price must be, relative to the close, to count as the day's closing price.</summary>
    public static readonly TimeSpan NearTheClose = TimeSpan.FromMinutes(15);

    /// <summary>Who the settlement's signal is attributed to.</summary>
    public const string By = "expiry-settlement";

    private readonly TradingDbContext _dbContext;
    private readonly IPaperTradingService _paperTrading;
    private readonly IMarketSessionService _sessions;
    private readonly ILogger<ExpirySettler> _logger;

    // Positions already reported as unpriceable, so a missing close is logged
    // once per API lifetime rather than every five minutes for ever.
    private static readonly HashSet<long> ReportedUnpriced = new();
    private static readonly object ReportedGate = new();

    public ExpirySettler(
        TradingDbContext dbContext,
        IPaperTradingService paperTrading,
        IMarketSessionService sessions,
        ILogger<ExpirySettler> logger)
    {
        _dbContext = dbContext;
        _paperTrading = paperTrading;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>What one pass did.</summary>
    public sealed record SweepResult(int Settled, int AwaitingPrice, int NotDueYet);

    /// <summary>One expired contract, as the settlement needs it.</summary>
    /// <param name="Kind">"CE", "PE" or "FUT".</param>
    public sealed record Contract(
        string Symbol,
        string Exchange,
        string Segment,
        string Kind,
        string Underlying,
        decimal? Strike,
        DateOnly Expiry);

    /// <summary>
    /// Settles every open manual-book position whose contract's expiry day
    /// closed at least <see cref="Grace"/> before <paramref name="nowUtc"/>.
    /// </summary>
    public async Task<SweepResult> SettleDueAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var books = _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.StrategyName == ManualOrdersController.BookStrategyName
                        && x.Mode == PaperTradingService.LivePaperMode)
            .Select(x => x.Id);

        var open = await _dbContext.PaperPositions.AsNoTracking()
            .Where(x => x.Status == "Open" && books.Contains(x.SimulationRunId))
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (open.Count == 0) return new SweepResult(0, 0, 0);

        var contracts = await ResolveContractsAsync(open.Select(x => x.Symbol), cancellationToken);

        int settled = 0, awaiting = 0, notDue = 0;
        foreach (var position in open)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!contracts.TryGetValue(position.Symbol, out var contract)) continue;

            var closeUtc = ExpiryCloseUtc(contract);
            if (nowUtc < closeUtc + Grace)
            {
                notDue++;
                continue;
            }

            string referenceSymbol = await ReferenceSymbolAsync(contract, cancellationToken);
            var observed = string.IsNullOrWhiteSpace(referenceSymbol)
                ? null
                : await ClosingPriceAsync(referenceSymbol, contract.Expiry, closeUtc, cancellationToken);

            if (observed is null)
            {
                awaiting++;
                bool first;
                lock (ReportedGate) first = ReportedUnpriced.Add(position.Id);
                if (first)
                {
                    _logger.LogWarning(
                        "Position {PositionId} ({Symbol}) in manual book {RunId} expired on {Expiry} but {Reference} has no price near that day's close; it stays open until one is found.",
                        position.Id, position.Symbol, position.SimulationRunId, contract.Expiry, string.IsNullOrWhiteSpace(referenceSymbol) ? "its underlying" : referenceSymbol);
                }
                continue;
            }

            decimal price = SettlementPrice(contract.Kind, contract.Strike, observed.Price);
            string reason = Reason(contract, referenceSymbol, observed, price);
            string metadata = JsonSerializer.Serialize(new
            {
                reason,
                by = By,
                system = true,
                expirySettlement = true,
                expiry = contract.Expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                kind = contract.Kind,
                strike = contract.Strike,
                underlyingSymbol = referenceSymbol,
                underlyingPrice = observed.Price,
                underlyingPriceSource = observed.Source,
                settlementPrice = price
            });

            if (await _paperTrading.SettleExpiredPositionAsync(
                    position.SimulationRunId, position.Id, price, metadata, closeUtc, cancellationToken))
            {
                settled++;
                _logger.LogInformation(
                    "Settled position {PositionId} of manual book {RunId}: {Reason}",
                    position.Id, position.SimulationRunId, reason);
            }
        }

        return new SweepResult(settled, awaiting, notDue);
    }

    /// <summary>
    /// Symbols of carried manual-book positions that are not live on the
    /// recording list: the ones that would get no quote tomorrow.
    /// </summary>
    /// <remarks>
    /// A carried leg is marked against its live quote, and the feed quotes only
    /// what is on the recording list. The ticket subscribes a contract when it
    /// is first looked at, and the list keeps it until it expires — but a row
    /// removed by hand (the "silent" prune, a watchlist edit) would leave a
    /// position the book still holds marked at yesterday's price every
    /// morning, with nothing to say why. Expired contracts are left out: there
    /// is nothing to subscribe to, and the settlement closes them.
    /// </remarks>
    public async Task<IReadOnlyList<string>> CarriedSymbolsOffTheFeedAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var books = _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.StrategyName == ManualOrdersController.BookStrategyName
                        && x.Mode == PaperTradingService.LivePaperMode)
            .Select(x => x.Id);

        var symbols = await _dbContext.PaperPositions.AsNoTracking()
            .Where(x => x.Status == "Open" && books.Contains(x.SimulationRunId))
            .Select(x => x.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (symbols.Count == 0) return Array.Empty<string>();

        var today = IstTime.DateOf(nowUtc);
        var expiries = await _dbContext.Instruments.AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol) && x.ExpiryDate != null)
            .Select(x => new { x.Symbol, x.ExpiryDate })
            .ToListAsync(cancellationToken);
        var expired = expiries
            .Where(x => x.ExpiryDate!.Value < today)
            .Select(x => x.Symbol)
            .ToHashSet(StringComparer.Ordinal);

        var live = await _dbContext.LiveWatchlistItems.AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol) && x.IsActive)
            .Select(x => x.Symbol)
            .ToListAsync(cancellationToken);
        var onTheList = live.ToHashSet(StringComparer.Ordinal);

        return symbols
            .Where(s => !expired.Contains(s) && !onTheList.Contains(s))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    // ------------------------------------------------------------ pure rules --

    /// <summary>
    /// What an expired contract settles at: an option's intrinsic value
    /// against <paramref name="underlyingPrice"/>, a future's own price.
    /// </summary>
    public static decimal SettlementPrice(string kind, decimal? strike, decimal underlyingPrice) => kind switch
    {
        "CE" => Math.Max(0m, underlyingPrice - (strike ?? 0m)),
        "PE" => Math.Max(0m, (strike ?? 0m) - underlyingPrice),
        _ => underlyingPrice
    };

    /// <summary>The activity line: "Expired — settled at intrinsic 150.35, S=24,650.35 (…)".</summary>
    public static string Reason(Contract contract, string referenceSymbol, ObservedPrice observed, decimal price)
    {
        var inv = CultureInfo.InvariantCulture;
        string day = contract.Expiry.ToString("d MMM", inv);
        string s = observed.Price.ToString("#,0.00", inv);

        if (contract.Kind == "FUT")
            return $"Expired — settled at the future's last price {s} ({observed.Source}, {day})";

        string strike = (contract.Strike ?? 0m).ToString("#,0.##", inv);
        string moneyness = price > 0m ? string.Empty : " (out of the money)";
        string of = referenceSymbol.StartsWith("MCX:", StringComparison.OrdinalIgnoreCase)
            ? $"{referenceSymbol} close"
            : $"{contract.Underlying} close";
        return $"Expired — settled at intrinsic {price.ToString("#,0.00", inv)}{moneyness}, "
               + $"S={s} ({of} {day}, {observed.Source}), K={strike} {contract.Kind}";
    }

    // -------------------------------------------------------------- lookups --

    /// <summary>A price and where it came from, for the reason line.</summary>
    public sealed record ObservedPrice(decimal Price, string Source);

    /// <summary>
    /// Expiry, strike and kind of each symbol: the instrument master first
    /// (expired rows are kept there, disabled), then the FYERS symbol grammar.
    /// A monthly symbol parsed without the master reads as expiring on the last
    /// calendar day of its month — never earlier than the real expiry, so a
    /// settlement can only come late, never early. Anything that is neither an
    /// option nor a future is left out: an equity never expires.
    /// </summary>
    internal async Task<Dictionary<string, Contract>> ResolveContractsAsync(IEnumerable<string> symbols, CancellationToken cancellationToken)
    {
        var wanted = symbols.Distinct(StringComparer.Ordinal).ToList();
        var rows = await _dbContext.Instruments.AsNoTracking()
            .Where(x => wanted.Contains(x.Symbol))
            .Select(x => new { x.Symbol, x.Exchange, x.Segment, x.InstrumentType, x.OptionType, x.Underlying, x.StrikePrice, x.ExpiryDate })
            .ToListAsync(cancellationToken);
        var bySymbol = rows
            .GroupBy(x => x.Symbol, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var result = new Dictionary<string, Contract>(StringComparer.Ordinal);
        foreach (var symbol in wanted)
        {
            string exchange = ExchangeOf(symbol);
            if (bySymbol.TryGetValue(symbol, out var row) && row.ExpiryDate is { } expiry)
            {
                string kind = KindOf(row.InstrumentType, row.OptionType);
                if (kind.Length == 0) continue;
                string underlying = string.IsNullOrWhiteSpace(row.Underlying)
                    ? UnderlyingCatalog.InferUnderlying(symbol)
                    : row.Underlying.Trim().ToUpperInvariant();
                result[symbol] = new Contract(
                    symbol,
                    string.IsNullOrWhiteSpace(row.Exchange) ? exchange : row.Exchange.Trim().ToUpperInvariant(),
                    string.IsNullOrWhiteSpace(row.Segment) ? DefaultSegment(exchange) : row.Segment.Trim().ToUpperInvariant(),
                    kind, underlying, row.StrikePrice, expiry);
                continue;
            }

            var parsed = UnderlyingCatalog.ParseOptionSymbol(symbol);
            if (parsed?.Expiry is { } parsedExpiry)
            {
                result[symbol] = new Contract(
                    symbol, exchange, DefaultSegment(exchange), parsed.OptionType, parsed.Underlying, parsed.Strike, parsedExpiry);
            }
        }

        return result;
    }

    /// <summary>
    /// The instrument S is read from: the index (or share) an NSE/BSE option is
    /// written on, the future an MCX option is written on, a future itself.
    /// </summary>
    internal async Task<string> ReferenceSymbolAsync(Contract contract, CancellationToken cancellationToken)
    {
        if (contract.Kind == "FUT") return contract.Symbol;

        if (contract.Exchange == "MCX")
        {
            // The future of the same commodity that is still alive on the
            // option's expiry: CRUDEOIL's September options expire on the 17th
            // and are written on the September future, which runs to the 21st.
            return await _dbContext.Instruments.AsNoTracking()
                .Where(x => x.Underlying == contract.Underlying
                            && x.InstrumentType == "FUT"
                            && x.Exchange == "MCX"
                            && x.ExpiryDate != null
                            && x.ExpiryDate >= contract.Expiry)
                .OrderBy(x => x.ExpiryDate)
                .Select(x => x.Symbol)
                .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
        }

        return UnderlyingCatalog.SpotSymbolFor(contract.Underlying);
    }

    /// <summary>
    /// The underlying's last price on <paramref name="day"/>, when the platform
    /// saw it within <see cref="NearTheClose"/> of <paramref name="closeUtc"/>.
    /// </summary>
    internal async Task<ObservedPrice?> ClosingPriceAsync(string symbol, DateOnly day, DateTime closeUtc, CancellationToken cancellationToken)
    {
        var dayStartUtc = IstTime.StartOfDayUtc(day);
        var dayEndUtc = IstTime.StartOfDayUtc(day.AddDays(1));
        var nearUtc = closeUtc - NearTheClose;

        // Bars and candles are cut at the close: after it, the feeds have been
        // seen writing flat closing-price candles until 20:00 IST.
        var bar = await _dbContext.LiveBars.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Resolution == "1m"
                        && x.BarStartUtc >= nearUtc && x.BarStartUtc < closeUtc)
            .OrderByDescending(x => x.BarStartUtc)
            .Select(x => (decimal?)x.Close)
            .FirstOrDefaultAsync(cancellationToken);
        if (bar is > 0m) return new ObservedPrice(bar.Value, "last recorded bar");

        var candle = await _dbContext.Candles.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Resolution != "D"
                        && x.TimeStampUtc >= nearUtc && x.TimeStampUtc < closeUtc)
            .OrderByDescending(x => x.TimeStampUtc)
            .Select(x => (decimal?)x.Close)
            .FirstOrDefaultAsync(cancellationToken);
        if (candle is > 0m) return new ObservedPrice(candle.Value, "last intraday candle");

        var quote = await _dbContext.LiveQuotesLatest.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.UpdatedUtc >= nearUtc && x.UpdatedUtc < dayEndUtc)
            .Select(x => x.LastTradedPrice)
            .FirstOrDefaultAsync(cancellationToken);
        if (quote is > 0m) return new ObservedPrice(quote.Value, "last live price");

        var daily = await _dbContext.Candles.AsNoTracking()
            .Where(x => x.Symbol == symbol && x.Resolution == "D"
                        && x.TimeStampUtc >= dayStartUtc && x.TimeStampUtc < dayEndUtc)
            .OrderByDescending(x => x.TimeStampUtc)
            .Select(x => (decimal?)x.Close)
            .FirstOrDefaultAsync(cancellationToken);
        if (daily is > 0m) return new ObservedPrice(daily.Value, "daily candle");

        return null;
    }

    internal DateTime ExpiryCloseUtc(Contract contract) =>
        ExpiryCloseUtc(_sessions, contract.Exchange, contract.Segment, contract.Expiry);

    /// <summary>
    /// When a contract's expiry day closes on its exchange: 15:30 IST for NSE
    /// and BSE, the evening close for MCX (which follows New York's clocks).
    /// Also the moment the live greeks count time to expiry to.
    /// </summary>
    public static DateTime ExpiryCloseUtc(IMarketSessionService sessions, string exchange, string segment, DateOnly expiry)
    {
        var middayUtc = IstTime.FromIst(expiry.ToDateTime(new TimeOnly(12, 0)));
        try
        {
            return sessions.GetSessionInfo(middayUtc, exchange, segment).SessionCloseUtc;
        }
        catch (NotSupportedException)
        {
            // An exchange or segment the session rules do not know (NSE's
            // currency segment closes at 17:00): assume the latest close any
            // market here has, MCX's 23:55, so a settlement can only come late,
            // never while the contract may still be trading.
            return IstTime.FromIst(expiry.ToDateTime(new TimeOnly(23, 55)));
        }
    }

    private static string KindOf(string? instrumentType, string? optionType)
    {
        string type = (instrumentType ?? string.Empty).Trim().ToUpperInvariant();
        if (type is "CE" or "PE" or "FUT") return type;
        string option = (optionType ?? string.Empty).Trim().ToUpperInvariant();
        return option is "CE" or "PE" ? option : string.Empty;
    }

    private static string ExchangeOf(string symbol)
    {
        int colon = symbol.IndexOf(':');
        return colon > 0 ? symbol[..colon].Trim().ToUpperInvariant() : "NSE";
    }

    private static string DefaultSegment(string exchange) => exchange == "MCX" ? "COM" : "FO";
}
