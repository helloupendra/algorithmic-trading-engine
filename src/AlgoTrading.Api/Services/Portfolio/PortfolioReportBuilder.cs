using System.Globalization;
using AlgoTrading.Contracts.Portfolio;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Portfolio;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Api.Services.Portfolio;

/// <summary>One broker's last good snapshot, as the report reads it.</summary>
/// <param name="LastSyncOk">Whether the broker's latest read worked; false when one failed after this snapshot was taken.</param>
/// <param name="AttemptedUtc">When that failed read was made.</param>
/// <param name="Disconnected">The broker was disconnected without forget: its last figures stay, marked as such.</param>
public sealed record PortfolioBrokerSnapshot(
    string Broker,
    long SnapshotId,
    DateTime TakenUtc,
    BrokerPortfolio Portfolio,
    bool LastSyncOk = true,
    string? LastSyncError = null,
    DateTime? AttemptedUtc = null,
    bool Disconnected = false);

/// <summary>A holding's sector and market-cap band, when the desk knows them.</summary>
public sealed record PortfolioClass(string? Sector, string? MarketCapBand)
{
    public static readonly PortfolioClass Unknown = new(null, null);
}

/// <summary>Everything a report is computed from.</summary>
/// <param name="Closes">Completed sessions' daily closes, by the desk's symbol.</param>
/// <param name="Index">NIFTY 50's completed sessions' closes.</param>
/// <param name="Notes">Notes the caller already has: a broker connected and not read yet, a sign-in that ended.</param>
/// <param name="Classify">Sector and market-cap band by ISIN; every holding is Unknown without it.</param>
public sealed record PortfolioReportInputs(
    DateTime NowUtc,
    IReadOnlyList<PortfolioBrokerSnapshot> Snapshots,
    IReadOnlyDictionary<string, IReadOnlyList<DailyClose>> Closes,
    IReadOnlyList<DailyClose> Index,
    int MaxSessions = 250,
    IReadOnlyList<string>? Notes = null,
    Func<string, PortfolioClass>? Classify = null);

/// <summary>
/// The portfolio report: holdings merged across brokers by ISIN, valued, weighted, grouped, and measured. Pure: the
/// same snapshots and closes give the same report, which is what lets a summary written at one sync be checked again
/// against the report shown later.
/// </summary>
/// <remarks>
/// <para>
/// A holding held at two brokers is one holding: the quantities and the amounts paid add up, the average price is
/// the total paid over the total quantity, and the price is the freshest broker price among them (a broker price
/// over the desk's stored close). A holding with no price at all is listed but left out of the value, the P&amp;L
/// and the weights, and the notes say so; it is never valued at its cost.
/// </para>
/// <para>
/// Facts only: nothing here grades, ranks or suggests. The notes say plainly what is missing.
/// </para>
/// </remarks>
public static class PortfolioReportBuilder
{
    public const string Disclaimer = "Information about your own holdings, not investment advice.";

    public const string UnknownClass = "Unknown";

    /// <summary>A broker's figures older than this are called out as old.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private sealed record Merged(
        string Key,
        string Symbol,
        string Isin,
        string? Name,
        string? CanonicalSymbol,
        IReadOnlyList<string> Brokers,
        IReadOnlyList<(string Broker, decimal Qty)> ByBroker,
        decimal Qty,
        decimal Invested,
        BrokerHolding? Priced,
        PortfolioClass Class);

    public static PortfolioReport Build(PortfolioReportInputs inputs)
    {
        var notes = new List<string>(inputs.Notes ?? []);
        var classify = inputs.Classify ?? (_ => PortfolioClass.Unknown);

        var merged = Merge(inputs.Snapshots, classify);

        // Values and P&L, per holding.
        var rows = merged.Select(m =>
        {
            decimal? ltp = m.Priced?.Ltp;
            decimal? value = ltp is decimal p ? Money(m.Qty * p) : null;
            decimal? pnl = value is decimal v ? Money(v - m.Invested) : null;
            decimal? pnlPct = pnl is decimal gain && m.Invested > 0 ? Pct(gain / m.Invested) : null;
            decimal? day = ltp is decimal now && m.Priced?.PrevClose is decimal prev ? Money(m.Qty * (now - prev)) : null;
            decimal? prevValue = m.Priced?.PrevClose is decimal pc && day is not null ? m.Qty * pc : null;
            return (Holding: m, Ltp: ltp, Value: value, Pnl: pnl, PnlPct: pnlPct, DayChange: day, PrevValue: prevValue);
        }).ToList();

        var priced = rows.Where(r => r.Value is not null).ToList();
        decimal current = priced.Sum(r => r.Value!.Value);
        decimal invested = Money(priced.Sum(r => r.Holding.Invested));
        decimal totalPnl = Money(current - invested);
        var withDay = rows.Where(r => r.DayChange is not null).ToList();
        decimal? dayChange = withDay.Count > 0 ? Money(withDay.Sum(r => r.DayChange!.Value)) : null;
        decimal prevValueSum = withDay.Sum(r => r.PrevValue ?? 0);
        decimal? dayChangePct = dayChange is decimal dc && prevValueSum > 0 ? Pct(dc / prevValueSum) : null;

        decimal? Weight(decimal? value) => value is decimal v && current > 0 ? Fraction(v / current) : null;

        var holdings = rows
            .OrderByDescending(r => r.Value ?? -1)
            .ThenBy(r => r.Holding.Symbol, StringComparer.Ordinal)
            .Select(r => new PortfolioHoldingView(
                r.Holding.Symbol,
                r.Holding.Isin,
                r.Holding.Name,
                r.Holding.Brokers,
                r.Holding.Qty,
                r.Holding.Qty > 0 ? Price(r.Holding.Invested / r.Holding.Qty) : 0,
                r.Ltp,
                Money(r.Holding.Invested),
                r.Value,
                r.Pnl,
                r.PnlPct,
                r.DayChange,
                Weight(r.Value),
                r.Holding.Class.Sector ?? UnknownClass,
                r.Holding.Class.MarketCapBand ?? UnknownClass))
            .ToList();

        var totals = new PortfolioTotals(invested, Money(current), totalPnl, invested > 0 ? Pct(totalPnl / invested) : null,
            dayChange, dayChangePct, holdings.Count);

        // Where the value sits.
        var pricedViews = holdings.Where(h => h.Value is not null).ToList();
        var bySector = Slices(pricedViews.GroupBy(h => h.Sector).Select(g => (g.Key, g.Sum(h => h.Value!.Value))), current);
        var byBand = Slices(pricedViews.GroupBy(h => h.MarketCapBand).Select(g => (g.Key, g.Sum(h => h.Value!.Value))), current);
        var byBroker = Slices(
            priced.SelectMany(r => r.Holding.ByBroker.Select(b => (b.Broker, Value: b.Qty * r.Ltp!.Value)))
                .GroupBy(x => x.Broker)
                .Select(g => (PortfolioBrokers.NameOf(g.Key), g.Sum(x => x.Value))),
            current);

        var weights = pricedViews.Select(h => h.Weight!.Value).OrderByDescending(w => w).ToList();
        var concentration = new PortfolioConcentration(
            weights.Count > 0 ? weights[0] : null,
            weights.Count > 0 ? Fraction(pricedViews.Take(5).Sum(h => h.Value!.Value) / current) : null,
            weights.Count > 0 ? Fraction(pricedViews.Sum(h => Sq(h.Value!.Value / current))) : null,
            pricedViews.Take(5).Select(h => h.Symbol).ToList());

        // Risk at today's weights over past closes.
        var riskHoldings = priced.Select(r => new RiskHolding(
            r.Holding.Symbol,
            r.Value!.Value,
            r.Holding.CanonicalSymbol is string s && inputs.Closes.TryGetValue(s, out var closes)
                ? closes.GroupBy(c => c.Date).ToDictionary(g => g.Key, g => g.Last().Close)
                : null)).ToList();
        var risk = PortfolioRiskMath.Compute(riskHoldings, inputs.Index, inputs.MaxSessions);
        notes.AddRange(risk.Gaps);

        // Positions and the money in each account.
        var positions = inputs.Snapshots
            .SelectMany(s => s.Portfolio.Positions.Select(p => new PortfolioPositionView(
                s.Broker, p.Symbol, p.Product, p.Quantity, p.AvgPrice, p.Ltp, p.Pnl, p.IsDerivative)))
            .ToList();
        var open = positions.Where(p => p.IsDerivative && p.Qty != 0).ToList();
        var openPriced = open.Where(p => p.Ltp is not null).ToList();
        decimal gross = Money(openPriced.Sum(p => Math.Abs(p.Qty) * p.Ltp!.Value));
        string derivativesNote = open.Count == 0
            ? "No open futures or options positions."
            : "Gross exposure is the market value of the open futures and options positions at their own last price: the " +
              "notional value for a future, the premium for an option." +
              (openPriced.Count < open.Count
                  ? $" {open.Count - openPriced.Count} of {open.Count} open {(open.Count == 1 ? "position has" : "positions have")} no price from the broker and {(open.Count - openPriced.Count == 1 ? "is" : "are")} left out of it."
                  : string.Empty);

        var funds = inputs.Snapshots
            .Where(s => s.Portfolio.Funds is not null)
            .Select(s => new PortfolioFundsView(s.Broker, s.Portfolio.Funds!.Available, s.Portfolio.Funds.Used, s.Portfolio.Funds.Total))
            .ToList();

        notes.InsertRange(0, HoldingNotes(rows.Select(r => (r.Holding, r.Value, r.DayChange)).ToList(), holdings));
        notes.AddRange(SourceNotes(inputs.Snapshots, inputs.NowUtc));

        return new PortfolioReport(
            inputs.Snapshots.Count > 0 ? inputs.Snapshots.Max(s => s.TakenUtc) : inputs.NowUtc,
            inputs.Snapshots.Select(Source).ToList(),
            totals,
            holdings,
            new PortfolioAllocation(bySector, byBroker, byBand),
            concentration,
            new PortfolioRisk(risk.Sessions, risk.VolatilityAnnualPct, risk.BetaVsNifty, risk.MaxDrawdownPct, risk.CoveredWeight, risk.Note),
            positions,
            new PortfolioDerivatives(open.Count, gross, derivativesNote),
            funds,
            null,
            notes.Distinct(StringComparer.Ordinal).ToList(),
            Disclaimer);
    }

    /// <summary>The symbol a person reads: no exchange in front, no series behind ("NSE:BAJAJ-AUTO-EQ" → "BAJAJ-AUTO").</summary>
    public static string DisplaySymbol(string symbol)
    {
        string s = symbol.Trim();
        int colon = s.IndexOf(':');
        if (colon >= 0) s = s[(colon + 1)..];
        foreach (string series in new[] { "-EQ", "-BE", "-BZ", "-SM", "-ST" })
        {
            if (s.EndsWith(series, StringComparison.OrdinalIgnoreCase)) return s[..^series.Length];
        }

        return s;
    }

    private static List<Merged> Merge(IReadOnlyList<PortfolioBrokerSnapshot> snapshots, Func<string, PortfolioClass> classify)
    {
        var members = snapshots
            .SelectMany(s => s.Portfolio.Holdings.Where(h => h.Quantity > 0).Select(h => (s.Broker, s.TakenUtc, Holding: h)))
            .ToList();

        return members
            // Same ISIN, same security, whichever broker holds it; a holding without an ISIN stands alone by its symbol.
            .GroupBy(m => m.Holding.Isin.Length > 0 ? m.Holding.Isin : $"{m.Broker}|{m.Holding.Exchange}|{m.Holding.Symbol}".ToUpperInvariant())
            .Select(g =>
            {
                var list = g.ToList();
                var named = list.FirstOrDefault(m => m.Holding.CanonicalSymbol is not null).Holding ?? list[0].Holding;

                // One price for the security: a broker's own over the desk's close, then the freshest snapshot. Its previous
                // close comes with it, so the day's change is never one price against another source's close.
                var priced = list
                    .Where(m => m.Holding.Ltp is > 0)
                    .OrderByDescending(m => m.Holding.PriceSource == PriceSources.Broker)
                    .ThenByDescending(m => m.TakenUtc)
                    .Select(m => m.Holding)
                    .FirstOrDefault();

                decimal qty = list.Sum(m => m.Holding.Quantity);
                decimal invested = list.Sum(m => m.Holding.Quantity * m.Holding.AvgPrice);
                string isin = list[0].Holding.Isin;
                return new Merged(
                    g.Key,
                    DisplaySymbol(named.CanonicalSymbol ?? named.Symbol),
                    isin,
                    list.Select(m => m.Holding.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                    named.CanonicalSymbol,
                    list.Select(m => m.Broker).Distinct().ToList(),
                    list.GroupBy(m => m.Broker).Select(b => (b.Key, b.Sum(m => m.Holding.Quantity))).ToList(),
                    qty,
                    invested,
                    priced,
                    isin.Length > 0 ? classify(isin) : PortfolioClass.Unknown);
            })
            .ToList();
    }

    private static IEnumerable<string> HoldingNotes(
        IReadOnlyList<(Merged Holding, decimal? Value, decimal? DayChange)> rows,
        IReadOnlyList<PortfolioHoldingView> views)
    {
        if (rows.Count == 0) yield break;

        var unpriced = rows.Where(r => r.Value is null).Select(r => r.Holding.Symbol).ToList();
        if (unpriced.Count > 0)
        {
            yield return $"{Count(unpriced.Count, "holding has", "holdings have")} no price ({List(unpriced)}): " +
                         "left out of the current value, the P&L and the weights.";
        }

        var deskPriced = rows.Where(r => r.Holding.Priced?.PriceSource == PriceSources.DeskClose).ToList();
        if (deskPriced.Count > 0)
        {
            string dates = string.Join(", ", deskPriced.Select(r => r.Holding.Priced!.PriceDate).Where(d => d is not null)
                .Distinct().Order().Select(d => d!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            yield return $"{Count(deskPriced.Count, "holding is", "holdings are")} valued at the desk's stored daily close" +
                         $"{(dates.Length > 0 ? $" of {dates}" : string.Empty)}, because the broker sends no price with holdings: " +
                         List(deskPriced.Select(r => r.Holding.Symbol).ToList()) + ".";
        }

        var noDay = rows.Where(r => r.Value is not null && r.DayChange is null).Select(r => r.Holding.Symbol).ToList();
        if (noDay.Count > 0)
        {
            yield return $"The day's change is not known for {Count(noDay.Count, "holding", "holdings")} ({List(noDay)}): " +
                         "the desk has no stored close for the session before.";
        }

        int unknownSector = views.Count(v => v.Sector == UnknownClass);
        int unknownBand = views.Count(v => v.MarketCapBand == UnknownClass);
        if (unknownSector == views.Count && unknownBand == views.Count)
        {
            yield return "Sector and market-cap band are Unknown for every holding: the desk holds no sector or market-cap classification for listed stocks yet.";
        }
        else
        {
            if (unknownSector > 0) yield return $"The sector is Unknown for {Count(unknownSector, "holding", "holdings")}.";
            if (unknownBand > 0) yield return $"The market-cap band is Unknown for {Count(unknownBand, "holding", "holdings")}.";
        }
    }

    /// <summary>
    /// A broker's line in <c>sources</c>: its last good read, and when the latest attempt failed or the broker was
    /// disconnected, what happened.
    /// </summary>
    private static PortfolioSource Source(PortfolioBrokerSnapshot s)
    {
        if (s.Disconnected) return new PortfolioSource(s.Broker, s.TakenUtc, false, $"disconnected; last read {Ist(s.TakenUtc)}", null);
        return s.LastSyncOk
            ? new PortfolioSource(s.Broker, s.TakenUtc, true, null, null)
            : new PortfolioSource(s.Broker, s.TakenUtc, false, s.LastSyncError, s.AttemptedUtc);
    }

    private static IEnumerable<string> SourceNotes(IReadOnlyList<PortfolioBrokerSnapshot> snapshots, DateTime nowUtc)
    {
        foreach (var s in snapshots)
        {
            string name = PortfolioBrokers.NameOf(s.Broker);
            if (s.Disconnected)
            {
                yield return $"{name}: disconnected; last read {Ist(s.TakenUtc)}. Its last figures stay until it is disconnected with forget.";
            }
            else if (!s.LastSyncOk)
            {
                string when = s.AttemptedUtc is DateTime at ? $" ({Ist(at)})" : string.Empty;
                string why = string.IsNullOrWhiteSpace(s.LastSyncError) ? "." : $": {s.LastSyncError}";
                yield return $"{name}: showing {Ist(s.TakenUtc)} data; the latest sync{when} failed{why}";
            }
            else if (nowUtc - s.TakenUtc > StaleAfter)
            {
                yield return $"{name}: showing {Ist(s.TakenUtc)} data, more than a day old.";
            }
        }
    }

    private static string Ist(DateTime utc) => $"{IstTime.ToIst(utc):dd MMM HH:mm} IST";

    private static IReadOnlyList<PortfolioSlice> Slices(IEnumerable<(string Name, decimal Value)> groups, decimal total) =>
        groups
            .Where(g => g.Value > 0)
            .OrderByDescending(g => g.Value)
            .Select(g => new PortfolioSlice(g.Name, Money(g.Value), total > 0 ? Fraction(g.Value / total) : 0))
            .ToList();

    private static string Count(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private static string List(IReadOnlyList<string> symbols) =>
        string.Join(", ", symbols.Take(8)) + (symbols.Count > 8 ? ", …" : string.Empty);

    private static decimal Sq(decimal x) => x * x;

    private static decimal Money(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    private static decimal Price(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    /// <summary>A fraction as a percentage, to two decimals: for the fields named "...Pct".</summary>
    private static decimal Pct(decimal fraction) => Math.Round(fraction * 100, 2, MidpointRounding.AwayFromZero);

    /// <summary>A share of value, from 0 to 1, to four decimals: for the weights and the HHI.</summary>
    private static decimal Fraction(decimal fraction) => Math.Round(fraction, 4, MidpointRounding.AwayFromZero);
}
