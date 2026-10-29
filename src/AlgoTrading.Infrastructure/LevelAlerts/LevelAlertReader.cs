using System.Globalization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.LevelAlerts;

/// <summary>Where a rule's target trades, and whether it is trading now.</summary>
/// <param name="Known">False when the session service has no hours for the exchange: its rules are never read.</param>
/// <param name="Day">The IST trading day, which is the session: NSE, BSE and MCX each open and close inside one.</param>
public readonly record struct LevelSession(string Exchange, bool Known, bool Open, DateOnly Day, DateTime OpenUtc);

/// <summary>What one target reads now: a value when there is a fresh one, and in words why not when there is not.</summary>
/// <param name="State">
/// <c>live</c> (fresh, comparable), <c>stale</c> (older than the limit, or from before
/// the open), <c>no-quote</c>, <c>no-chain</c>, <c>no-call-oi</c> (a PCR with no calls
/// to divide by), <c>closed</c> (the exchange is not in session) or <c>unknown-exchange</c>.
/// </param>
/// <param name="Context">For a PCR, the expiry it was read on ("04 Nov expiry").</param>
public sealed record LevelReading(
    string Kind,
    string Target,
    string Exchange,
    bool InSession,
    decimal? Value,
    DateTime? AsOfUtc,
    string State,
    string? Detail,
    string? Context = null)
{
    public const string Live = "live";
    public const string Stale = "stale";
    public const string NoQuote = "no-quote";
    public const string NoChain = "no-chain";
    public const string NoCallOi = "no-call-oi";
    public const string Closed = "closed";
    public const string UnknownExchange = "unknown-exchange";

    public bool IsLive => State == Live && Value is not null && AsOfUtc is not null;
}

/// <summary>
/// Reads what the level alerts compare against, from what the desk already
/// holds: <c>live_quotes_latest</c> for prices (the feeds write it on every
/// tick) and the recorder's chain captures for PCR. Never a vendor call.
/// </summary>
/// <remarks>
/// Shared by the service and the page's "now" reading, so the number a user
/// sees beside the form is the number the rule will be compared with.
/// </remarks>
public sealed class LevelAlertReader
{
    private readonly TradingDbContext _db;
    private readonly IMarketSessionService _sessions;

    public LevelAlertReader(TradingDbContext db, IMarketSessionService sessions)
    {
        _db = db;
        _sessions = sessions;
    }

    /// <summary>A rule's key in the service's memory and the page's list: "price:NSE:NIFTY50-INDEX", "pcr:NIFTY".</summary>
    public static string KeyOf(string kind, string target) => $"{kind}:{target}";

    /// <summary>
    /// The exchange whose hours gate a target: the symbol's own prefix for a
    /// price; for a PCR, MCX for a commodity and the index's exchange otherwise
    /// (BSE for SENSEX and BANKEX).
    /// </summary>
    public static string ExchangeOf(string kind, string target)
    {
        if (kind == LevelAlertKinds.Pcr)
        {
            if (UnderlyingCatalog.IsCommodity(target)) return "MCX";
            target = UnderlyingCatalog.SpotSymbolFor(target);
        }

        int colon = target.IndexOf(':');
        return colon > 0 ? target[..colon].ToUpperInvariant() : string.Empty;
    }

    /// <summary>
    /// MCX's one segment; NSE and BSE's derivatives segment for a future, an
    /// option or a chain, the cash segment otherwise. The two trade the same
    /// hours today; from 7 Dec 2026 index derivatives close at 15:45, and the
    /// session service is where that will be said.
    /// </summary>
    public static string SegmentOf(string kind, string target, string exchange)
    {
        if (exchange == "MCX") return "COM";
        if (kind == LevelAlertKinds.Pcr) return "FO";
        return target.EndsWith("FUT", StringComparison.OrdinalIgnoreCase) || UnderlyingCatalog.ParseOptionSymbol(target) is not null
            ? "FO"
            : "CM";
    }

    /// <summary>Every exchange and segment the session service has hours for.</summary>
    private static readonly (string Exchange, string Segment)[] Markets =
        [("NSE", "CM"), ("NSE", "FO"), ("BSE", "CM"), ("BSE", "FO"), ("MCX", "COM")];

    /// <summary>Whether any of them is trading now. In memory: the calendar is loaded once, not read per call.</summary>
    public bool AnyExchangeOpen(DateTime nowUtc) => Markets.Any(m =>
    {
        try
        {
            return _sessions.IsMarketOpen(nowUtc, m.Exchange, m.Segment);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return false;
        }
    });

    /// <summary>The target's session now; never throws, so one exchange without hours cannot stop the others.</summary>
    public LevelSession SessionOf(string kind, string target, DateTime nowUtc)
    {
        string exchange = ExchangeOf(kind, target);
        try
        {
            var info = _sessions.GetSessionInfo(nowUtc, exchange, SegmentOf(kind, target, exchange));
            return new LevelSession(exchange, true, info.IsTradingDay && info.IsMarketOpen, IstTime.DateOf(nowUtc),
                DateTime.SpecifyKind(info.SessionOpenUtc, DateTimeKind.Utc));
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            return new LevelSession(exchange, false, false, IstTime.DateOf(nowUtc), nowUtc);
        }
    }

    /// <summary>
    /// The last traded price of each symbol, in one query. A quote older than
    /// <see cref="LevelCross.PriceFreshFor"/>, or from before its session
    /// opened, is <c>stale</c>: shown, never compared.
    /// </summary>
    public async Task<Dictionary<string, LevelReading>> PricesAsync(
        IReadOnlyCollection<string> symbols,
        IReadOnlyDictionary<string, LevelSession> sessions,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, LevelReading>(StringComparer.Ordinal);
        if (symbols.Count == 0) return result;

        var rows = await _db.LiveQuotesLatest.AsNoTracking()
            .Where(q => symbols.Contains(q.Symbol))
            .Select(q => new { q.Symbol, q.LastTradedPrice, q.UpdatedUtc, q.ExchangeTimestampUtc })
            .ToListAsync(cancellationToken);
        var bySymbol = rows.ToDictionary(r => r.Symbol, StringComparer.Ordinal);

        foreach (var symbol in symbols)
        {
            var session = sessions[symbol];
            if (!bySymbol.TryGetValue(symbol, out var row) || row.LastTradedPrice is not > 0)
            {
                result[symbol] = new LevelReading(LevelAlertKinds.Price, symbol, session.Exchange, session.Open, null, null,
                    LevelReading.NoQuote, "no live quote: not streamed by any feed");
                continue;
            }

            var asOf = LevelCross.QuoteTime(row.UpdatedUtc, row.ExchangeTimestampUtc);
            var (state, detail) = Freshness(asOf, session, nowUtc, LevelCross.PriceFreshFor, "quote");
            result[symbol] = new LevelReading(LevelAlertKinds.Price, symbol, session.Exchange, session.Open,
                row.LastTradedPrice, asOf, state, detail);
        }

        return result;
    }

    /// <summary>
    /// The put-call ratio of an underlying's nearest-expiry chain, at its
    /// newest capture: total put OI over total call OI, as the chain page and
    /// its OI history compute it, rounded to the page's two decimals.
    /// </summary>
    /// <param name="known">
    /// The reading the caller already holds. When the newest capture is the
    /// one it was read from, its value is returned without adding the chain
    /// up again: the recorder captures once a minute and the service looks
    /// every few seconds.
    /// </param>
    /// <param name="valueWhenStale">
    /// Add up a capture that is too old to compare, too. The page wants the
    /// last PCR after the close; the service has no use for it, and would
    /// otherwise add the same old capture up every few seconds once the
    /// recorder stops.
    /// </param>
    public async Task<LevelReading> PcrAsync(
        string underlying,
        LevelSession session,
        DateTime nowUtc,
        LevelSample? known,
        bool valueWhenStale,
        CancellationToken cancellationToken)
    {
        var expiry = await new OptionChainService(_db).DefaultExpiryAsync(underlying, null, cancellationToken);
        if (expiry is not { } expiryDate)
        {
            return new LevelReading(LevelAlertKinds.Pcr, underlying, session.Exchange, session.Open, null, null,
                LevelReading.NoChain, "no chain recorded for this underlying");
        }

        string context = expiryDate.ToString("dd MMM", CultureInfo.InvariantCulture) + " expiry";
        var newest = await _db.OptionChainSnapshots.AsNoTracking()
            .Where(x => x.Underlying == underlying && x.ExpiryDate == expiryDate)
            .OrderByDescending(x => x.CapturedUtc)
            .Select(x => (DateTime?)x.CapturedUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (newest is not { } capturedRaw)
        {
            return new LevelReading(LevelAlertKinds.Pcr, underlying, session.Exchange, session.Open, null, null,
                LevelReading.NoChain, "no chain recorded for this underlying", context);
        }

        var captured = DateTime.SpecifyKind(capturedRaw, DateTimeKind.Utc);
        var (state, detail) = Freshness(captured, session, nowUtc, LevelCross.PcrFreshFor, "chain capture");
        if (state != LevelReading.Live && !valueWhenStale)
        {
            return new LevelReading(LevelAlertKinds.Pcr, underlying, session.Exchange, session.Open, null, captured, state, detail, context);
        }

        if (known is { } k && k.AsOfUtc == captured)
        {
            return new LevelReading(LevelAlertKinds.Pcr, underlying, session.Exchange, session.Open, k.Value, captured, state, detail, context);
        }

        var sides = await _db.OptionChainSnapshots.AsNoTracking()
            .Where(x => x.Underlying == underlying && x.ExpiryDate == expiryDate && x.CapturedUtc == capturedRaw)
            .GroupBy(x => x.OptionType)
            .Select(g => new { Type = g.Key, OpenInterest = g.Sum(x => x.OpenInterest ?? 0L) })
            .ToListAsync(cancellationToken);
        long calls = sides.Where(s => s.Type == "CE").Sum(s => s.OpenInterest);
        long puts = sides.Where(s => s.Type == "PE").Sum(s => s.OpenInterest);

        if (OptionChainAnalytics.PutCallRatio(puts, calls) is not { } pcr)
        {
            return new LevelReading(LevelAlertKinds.Pcr, underlying, session.Exchange, session.Open, null, captured,
                LevelReading.NoCallOi, "the capture has no call open interest, so no PCR", context);
        }

        return new LevelReading(LevelAlertKinds.Pcr, underlying, session.Exchange, session.Open, LevelCross.RoundPcr(pcr), captured,
            state, detail, context);
    }

    private static (string State, string? Detail) Freshness(DateTime asOfUtc, LevelSession session, DateTime nowUtc, TimeSpan freshFor, string what)
    {
        if (!session.Open) return (LevelReading.Closed, $"{session.Exchange} is not in session");
        if (asOfUtc < session.OpenUtc) return (LevelReading.Stale, $"the last {what} is from before the open");
        if (!LevelCross.IsFresh(asOfUtc, nowUtc, freshFor))
        {
            return (LevelReading.Stale, asOfUtc > nowUtc
                ? $"the last {what} is stamped ahead of the server clock"
                : $"the last {what} is {Age(nowUtc - asOfUtc)} old");
        }

        return (LevelReading.Live, null);
    }

    private static string Age(TimeSpan age) => age.TotalMinutes >= 1
        ? $"{(int)age.TotalMinutes} min"
        : $"{(int)age.TotalSeconds} s";
}
