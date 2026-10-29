// src/AlgoTrading.Api/Services/MarketCloseRules.cs
using System.Globalization;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Which live strategy runs the market-hours sweep squares off, and why: each
/// run at the close of the market it trades on, and never before it.
/// </summary>
/// <remarks>
/// <para>
/// Until 27 Sep the sweep stopped every run at 15:30 IST, so the morning plan's
/// two CrudeMomentum runs were squared off with eight hours of MCX left; and,
/// because it remembered "done today" only in memory, any API process started
/// after 15:30 stopped a crude run started by hand for the evening. The owner's
/// decision that day: crude trades the MCX evening, until the MCX close.
/// </para>
/// <para>
/// The rule is asked afresh every minute and needs no memory: a run is stopped
/// when the latest close of its own market, as the exchange calendar has it,
/// fell after the run started and is now past. So an NSE or BSE run started in
/// the morning stops at 15:30; a crude run at 23:30 while the US is on daylight
/// saving and 23:55 while it is not (<see cref="MarketSessionService"/>); a run
/// adopted after an API restart during the evening is judged exactly like one
/// that was never lost, and one adopted after its close is stopped at once. A
/// run started after its market's close (a replay run in the evening) is left
/// for the next close — as the old sweep, which fired once, left it.
/// </para>
/// <para>
/// A weekday on the exchange's holiday list still has a close: the usual hours
/// the calendar reports for it, so a run started by hand on an NSE holiday stops
/// at 15:30 as it always did. A Saturday or Sunday has none unless the calendar
/// holds a special session then.
/// </para>
/// <para>
/// An NSE or BSE run trades options, so its close is the derivatives segment's
/// (FO): 15:30 until 6 Dec 2026, 15:45 from 7 Dec (<see cref="EquityMarketHours"/>).
/// On an index's expiry day its expiring contracts stop at 15:30 while the rest
/// trade on, and a run on that index trades the expiring ones (the runner takes
/// the first expiry on or after today), so it still stops at 15:30 that day.
/// Stock runs trade to the derivatives close on every day. A strategy's own
/// square-off time is its rule and comes before any of these.
/// </para>
/// </remarks>
public static class MarketCloseRules
{
    public const string Mcx = "MCX";
    public const string Nse = "NSE";
    public const string Bse = "BSE";

    // Further back than any run of weekends and holidays; a weekday always has
    // a close, so the search ends within three days in practice.
    private const int LookBackDays = 7;

    /// <summary>A run on the desk, as the sweep needs to see it.</summary>
    public sealed record DeskRun(long RunId, string? Underlying, string? SpotSymbol, DateTime StartedUtc);

    /// <summary>A run whose market has closed since it started, with the reason to record.</summary>
    public sealed record RunToStop(long RunId, string Exchange, DateTime ClosedAtUtc, string Reason);

    /// <summary>
    /// The exchange whose session a run lives by. The run's price symbol is the
    /// witness (a crude run's is the near-month future, "MCX:CRUDEOIL26OCTFUT");
    /// a commodity underlying is MCX even when the symbol is missing, and
    /// anything else trades with NSE, or BSE when its symbol says so.
    /// </summary>
    public static string ExchangeOf(string? underlying, string? spotSymbol)
    {
        var symbol = (spotSymbol ?? string.Empty).Trim();
        if (symbol.StartsWith("MCX:", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(underlying) && UnderlyingCatalog.IsCommodity(underlying)))
        {
            return Mcx;
        }

        return symbol.StartsWith("BSE:", StringComparison.OrdinalIgnoreCase) ? Bse : Nse;
    }

    /// <summary>
    /// The latest close of the exchange's derivatives (or MCX's) at or before
    /// <paramref name="nowUtc"/>: today's, once it has passed, else the last
    /// earlier day that had one. Null only when the calendar has no close in the
    /// last week.
    /// </summary>
    public static DateTime? LastCloseUtc(IMarketSessionService sessions, DateTime nowUtc, string exchange)
        => LastCloseUtc(sessions, nowUtc, exchange, exchange == Mcx ? "COM" : "FO");

    /// <summary>The same for one segment: "CM" for NSE's or BSE's cash market, which closes at 15:30 on every day.</summary>
    public static DateTime? LastCloseUtc(IMarketSessionService sessions, DateTime nowUtc, string exchange, string segment)
    {
        var today = IstTime.DateOf(nowUtc);

        for (int back = 0; back <= LookBackDays; back++)
        {
            var day = today.AddDays(-back);
            // Any instant of the IST day answers for that day; noon keeps clear of its edges.
            var probe = back == 0 ? nowUtc : IstTime.FromIst(day.ToDateTime(new TimeOnly(12, 0)));
            var info = sessions.GetSessionInfo(probe, exchange, segment);

            bool hasClose = info.IsTradingDay || day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
            if (hasClose && info.SessionCloseUtc <= nowUtc)
            {
                return info.SessionCloseUtc;
            }
        }

        return null;
    }

    /// <summary>
    /// The latest close for a run or position holding an NSE or BSE index
    /// contract that expires today: today's cash close (15:30) once it has
    /// passed, when the expiring contracts stop, else the derivatives close of
    /// an earlier day. Before 7 Dec 2026 the two are the same close.
    /// </summary>
    public static DateTime? LastCloseOfExpiringUtc(IMarketSessionService sessions, DateTime nowUtc, string exchange)
    {
        var derivatives = LastCloseUtc(sessions, nowUtc, exchange, "FO");
        var cash = LastCloseUtc(sessions, nowUtc, exchange, "CM");
        return derivatives is null ? cash : cash is null ? derivatives : cash > derivatives ? cash : derivatives;
    }

    /// <summary>
    /// The runs to stop now: every run whose market's latest close came after
    /// it started. Each exchange's close is asked once. A run on an index in
    /// <paramref name="indicesExpiringToday"/> (<see cref="IndicesExpiringTodayAsync"/>)
    /// goes at the expiring contracts' close instead.
    /// </summary>
    public static IReadOnlyList<RunToStop> RunsToStop(
        IMarketSessionService sessions, DateTime nowUtc, IEnumerable<DeskRun> runs, IReadOnlySet<string>? indicesExpiringToday = null)
    {
        var closes = new Dictionary<(string Exchange, bool Expiring), DateTime?>();
        var due = new List<RunToStop>();

        foreach (var run in runs)
        {
            var exchange = ExchangeOf(run.Underlying, run.SpotSymbol);
            bool expiring = exchange != Mcx
                            && !string.IsNullOrWhiteSpace(run.Underlying)
                            && indicesExpiringToday?.Contains(run.Underlying.Trim()) == true;
            if (!closes.TryGetValue((exchange, expiring), out var close))
            {
                close = expiring ? LastCloseOfExpiringUtc(sessions, nowUtc, exchange) : LastCloseUtc(sessions, nowUtc, exchange);
                closes[(exchange, expiring)] = close;
            }

            if (close is DateTime closedAt && run.StartedUtc < closedAt)
            {
                due.Add(new RunToStop(run.RunId, exchange, closedAt, ReasonFor(exchange, closedAt)));
            }
        }

        return due;
    }

    /// <summary>
    /// "Market closed (15:30 IST)" for NSE and BSE — the words the console and
    /// Sentinel already read as a deliberate end — and "MCX closed (23:30 IST)"
    /// for commodities, with the close that actually applied ("Market closed
    /// (15:45 IST)" from 7 Dec 2026, but for an index's expiry day).
    /// </summary>
    public static string ReasonFor(string exchange, DateTime closedAtUtc)
    {
        var at = IstTime.ToIst(closedAtUtc).ToString("HH:mm", CultureInfo.InvariantCulture);
        return exchange == Mcx ? $"MCX closed ({at} IST)" : $"Market closed ({at} IST)";
    }

    /// <summary>
    /// Whether an expiry can bring a close forward at <paramref name="nowUtc"/>: on
    /// a day the derivatives trade past the cash close (from 7 Dec 2026), once
    /// that cash close has passed. Never before 7 Dec, so until then the sweep
    /// asks nothing more than it always did.
    /// </summary>
    public static bool ExpiryCanCloseFirst(DateTime nowUtc)
    {
        var ist = IstTime.ToIst(nowUtc);
        return EquityMarketHours.DerivativesCloseLater(DateOnly.FromDateTime(ist))
               && TimeOnly.FromDateTime(ist) >= EquityMarketHours.CashClose;
    }

    /// <summary>
    /// The index underlyings among <paramref name="underlyings"/> with a listed
    /// option expiring on the IST day of <paramref name="nowUtc"/>, as the runner
    /// would find them (<c>/api/Instruments/derivatives/expiries</c>), compared
    /// case-insensitively.
    /// </summary>
    /// <remarks>
    /// Asked only when the answer can change a close (<see cref="ExpiryCanCloseFirst"/>);
    /// any other time it is empty without touching the database.
    /// </remarks>
    public static async Task<IReadOnlySet<string>> IndicesExpiringTodayAsync(
        TradingDbContext db, DateTime nowUtc, IEnumerable<string?> underlyings, CancellationToken cancellationToken)
    {
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!ExpiryCanCloseFirst(nowUtc)) return none;
        var today = IstTime.DateOf(nowUtc);

        var indices = underlyings
            .Where(u => !string.IsNullOrWhiteSpace(u) && UnderlyingCatalog.IsIndex(u!))
            .Select(u => u!.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();
        if (indices.Count == 0) return none;

        var expiring = await db.Instruments.AsNoTracking()
            .Where(x => x.IsEnabled
                        && x.ExpiryDate == today
                        && (x.InstrumentType == "CE" || x.InstrumentType == "PE")
                        && indices.Contains(x.Underlying))
            .Select(x => x.Underlying)
            .Distinct()
            .ToListAsync(cancellationToken);
        return new HashSet<string>(expiring, StringComparer.OrdinalIgnoreCase);
    }
}
