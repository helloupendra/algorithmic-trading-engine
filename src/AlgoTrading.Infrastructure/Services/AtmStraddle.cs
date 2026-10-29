// src/AlgoTrading.Infrastructure/Services/AtmStraddle.cs
using AlgoTrading.Contracts.OptionChain;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// The at-the-money straddle and its IV, read off one capture of the chain,
/// and where an IV stands among the sessions before it.
/// </summary>
/// <remarks>
/// Kept apart from the data access, like <see cref="OptionChainAnalytics"/>:
/// which strike is the money, which price a leg is read at and what a rank
/// means are the parts a trader acts on, so they are pinned by a table of
/// inputs rather than by a database.
/// </remarks>
public static class AtmStraddle
{
    /// <summary>The leg was read at the mid of its bid and ask.</summary>
    public const string MidBasis = "mid";

    /// <summary>The leg was read at its last trade: no usable bid and ask.</summary>
    public const string LastTradeBasis = "ltp";

    /// <summary>One side of one strike in one capture, its IV already in percent.</summary>
    public readonly record struct ChainRow(
        DateTime CapturedUtc,
        decimal SpotPrice,
        decimal StrikePrice,
        string OptionType,
        decimal? LastTradedPrice,
        decimal? BidPrice,
        decimal? AskPrice,
        decimal? IvPercent,
        string SourceKey);

    /// <summary>When an expiry was first and last captured for an underlying.</summary>
    public readonly record struct ExpirySpan(DateOnly Expiry, DateTime FirstCapturedUtc, DateTime LastCapturedUtc);

    /// <summary>An IV against the closes of earlier sessions.</summary>
    /// <param name="Rank">Where it sits between the lowest and highest of them, 0 to 100; null when they span no range.</param>
    /// <param name="Percentile">The share of them below it, 0 to 100; null when there are none.</param>
    /// <param name="Days">How many sessions it was measured against.</param>
    public readonly record struct IvStanding(decimal? Rank, decimal? Percentile, int Days, decimal? Low, decimal? High);

    /// <summary>
    /// What one leg is worth: the mid of its bid and ask when both are quoted
    /// and not crossed, otherwise its last trade. The last trade on a quiet
    /// strike can be minutes old; the touch is what it could be dealt at in
    /// that minute. Null when neither is there.
    /// </summary>
    public static (decimal? Price, string? Basis) LegPrice(decimal? lastTraded, decimal? bid, decimal? ask)
    {
        if (bid is > 0m && ask is > 0m && ask >= bid) return ((bid.Value + ask.Value) / 2m, MidBasis);
        if (lastTraded is > 0m) return (lastTraded, LastTradeBasis);
        return (null, null);
    }

    /// <summary>
    /// The straddle in one capture: the strike nearest the spot, the call and
    /// put on it, their sum and their mean IV. Null when the capture has no
    /// spot to be at the money of.
    /// </summary>
    /// <remarks>
    /// The money is the chain page's own rule (<see cref="OptionChainAnalytics.AtTheMoney"/>),
    /// so the two never name different strikes for the same minute. A leg
    /// missing at that strike leaves the minute without a straddle rather than
    /// borrowing a strike further out: a straddle 50 points off the money is
    /// a different price, and a line that switched to it would show a jump
    /// that never traded.
    /// </remarks>
    public static StraddlePointResponse? Point(IReadOnlyCollection<ChainRow> capture)
    {
        if (capture is null || capture.Count == 0) return null;

        decimal spot = capture.Max(r => r.SpotPrice);
        if (spot <= 0) return null;

        var atm = OptionChainAnalytics.AtTheMoney(capture.Select(r => r.StrikePrice).Distinct().ToList(), spot);
        if (atm is null) return null;

        var call = Side(capture, atm.Value, "CE");
        var put = Side(capture, atm.Value, "PE");
        var (callPrice, callBasis) = call is { } c ? LegPrice(c.LastTradedPrice, c.BidPrice, c.AskPrice) : (null, null);
        var (putPrice, putBasis) = put is { } p ? LegPrice(p.LastTradedPrice, p.BidPrice, p.AskPrice) : (null, null);

        var ivs = new[] { call?.IvPercent, put?.IvPercent }
            .Where(x => x is > 0m)
            .Select(x => x!.Value)
            .ToList();

        return new StraddlePointResponse
        {
            CapturedUtc = capture.First().CapturedUtc,
            SpotPrice = spot,
            AtTheMoneyStrike = atm.Value,
            CallPrice = callPrice,
            CallPriceBasis = callBasis,
            PutPrice = putPrice,
            PutPriceBasis = putBasis,
            StraddlePrice = callPrice + putPrice,
            AtTheMoneyIv = ivs.Count == 0 ? null : ivs.Average(),
        };
    }

    /// <summary>
    /// A session's points, oldest first: one per capture, and one per minute
    /// when two recorders wrote the same minute (the later capture is kept),
    /// so a day read from both never zig-zags between their prices.
    /// </summary>
    public static List<StraddlePointResponse> Series(IEnumerable<ChainRow> rows)
        => rows
            .GroupBy(r => r.CapturedUtc)
            .Select(g => Point(g.ToList()))
            .OfType<StraddlePointResponse>()
            .GroupBy(p => MinuteOf(p.CapturedUtc))
            .Select(g => g.MaxBy(p => p.CapturedUtc)!)
            .OrderBy(p => p.CapturedUtc)
            .ToList();

    /// <summary>
    /// The expiries a day's chain may have been recorded for, nearest first:
    /// not behind the day (the recorder's own rule, <c>DhanChainRows.NearestExpiry</c>)
    /// and captured on or around it. The first with captures on the day itself
    /// is that day's chain.
    /// </summary>
    public static IEnumerable<DateOnly> ExpiryCandidates(IEnumerable<ExpirySpan> spans, DateOnly day)
        => spans
            .Where(s => s.Expiry >= day
                        && IstTime.DateOf(s.FirstCapturedUtc) <= day
                        && IstTime.DateOf(s.LastCapturedUtc) >= day)
            .OrderBy(s => s.Expiry)
            .Select(s => s.Expiry);

    /// <summary>
    /// IV rank and IV percentile of <paramref name="current"/> against the
    /// closes of earlier sessions.
    /// </summary>
    /// <remarks>
    /// Rank is where the value sits between the lowest and the highest of
    /// those closes, the value itself included, so it stays between 0 and 100
    /// on a day that sets a new extreme. Percentile is the share of those days
    /// that closed below it. Both are measured over whatever days there are
    /// and say how many: over a fortnight of history a "rank of 90" is a fact
    /// about a fortnight, and the count is what keeps it from reading as a
    /// year's.
    /// </remarks>
    public static IvStanding Standing(decimal current, IReadOnlyCollection<decimal> earlierCloses)
    {
        if (earlierCloses is null || earlierCloses.Count == 0) return new IvStanding(null, null, 0, null, null);

        decimal low = Math.Min(earlierCloses.Min(), current);
        decimal high = Math.Max(earlierCloses.Max(), current);
        decimal? rank = high > low ? (current - low) / (high - low) * 100m : null;
        decimal percentile = earlierCloses.Count(x => x < current) * 100m / earlierCloses.Count;

        return new IvStanding(rank, percentile, earlierCloses.Count, low, high);
    }

    private static ChainRow? Side(IReadOnlyCollection<ChainRow> capture, decimal strike, string optionType)
    {
        foreach (var row in capture)
        {
            if (row.StrikePrice == strike && string.Equals(row.OptionType, optionType, StringComparison.OrdinalIgnoreCase))
                return row;
        }
        return null;
    }

    private static DateTime MinuteOf(DateTime utc)
        => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, utc.Kind);
}
