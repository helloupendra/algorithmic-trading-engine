// src/AlgoTrading.Infrastructure/Services/OptionChainAnalytics.cs

namespace AlgoTrading.Infrastructure.Services;

/// <summary>What price and open interest together say about a strike.</summary>
public enum BuildUp
{
    /// <summary>Not enough moved to call it anything.</summary>
    Neutral,

    /// <summary>Price up, open interest up — new buyers.</summary>
    LongBuildUp,

    /// <summary>Price down, open interest up — new sellers.</summary>
    ShortBuildUp,

    /// <summary>Price up, open interest down — shorts buying back.</summary>
    ShortCovering,

    /// <summary>Price down, open interest down — longs leaving.</summary>
    LongUnwinding,
}

/// <summary>
/// The numbers an option chain shows that are not simply read off the feed.
/// </summary>
/// <remarks>
/// Kept apart from the data access on purpose: max pain and the build-up
/// classification are the parts a trader will actually act on, and they should
/// be provable against a table of inputs rather than against a database.
/// </remarks>
public static class OptionChainAnalytics
{
    /// <summary>
    /// How a strike is behaving, from the direction of price and of open interest.
    /// </summary>
    /// <remarks>
    /// The four names are the standard reading of the two signs together. Open
    /// interest is the count of contracts that exist: it rises when someone
    /// opens a new position and falls when one is closed, so pairing it with the
    /// price direction says whether the money coming in is buying or selling,
    /// and whether the money leaving was long or short.
    /// <para>
    /// Both changes must be real. A strike whose price moved a paisa on two
    /// contracts is not "building" anything, and labelling it would fill the
    /// column with noise exactly where the chain is thinnest.
    /// </para>
    /// </remarks>
    public static BuildUp Classify(
        decimal priceChange,
        long openInterestChange,
        decimal priceThreshold = 0m,
        long openInterestThreshold = 0)
    {
        bool priceMoved = Math.Abs(priceChange) > priceThreshold;
        bool interestMoved = Math.Abs(openInterestChange) > openInterestThreshold;

        if (!priceMoved || !interestMoved) return BuildUp.Neutral;

        bool priceUp = priceChange > 0;
        bool interestUp = openInterestChange > 0;

        return (priceUp, interestUp) switch
        {
            (true, true) => BuildUp.LongBuildUp,
            (false, true) => BuildUp.ShortBuildUp,
            (true, false) => BuildUp.ShortCovering,
            (false, false) => BuildUp.LongUnwinding,
        };
    }

    /// <summary>
    /// Put-call ratio: put open interest over call open interest.
    /// </summary>
    /// <remarks>
    /// Null rather than zero or infinity when there are no calls to divide by.
    /// A PCR of "0" reads as an extreme bullish signal; "no calls written here"
    /// is not a signal at all, and the two must not look alike.
    /// </remarks>
    public static decimal? PutCallRatio(long putOpenInterest, long callOpenInterest)
        => callOpenInterest > 0 ? (decimal)putOpenInterest / callOpenInterest : null;

    /// <summary>A change as a percentage of where it started.</summary>
    public static decimal? ChangePercent(long current, long baseline)
        => baseline > 0 ? (decimal)(current - baseline) / baseline * 100m : null;

    /// <summary>One strike's open interest on each side.</summary>
    public readonly record struct StrikeInterest(decimal Strike, long CallOpenInterest, long PutOpenInterest);

    /// <summary>
    /// The strike where option writers lose the least if the market expires there.
    /// </summary>
    /// <remarks>
    /// Computed the standard way: for each candidate strike, total what every
    /// call and put outstanding would pay out if the underlying settled there,
    /// and take the cheapest. Calls pay when settlement is above their strike,
    /// puts when it is below.
    /// <para>
    /// Null when nothing has open interest — an empty chain has no max pain, and
    /// returning the lowest strike would put a line on a chart that means
    /// nothing.
    /// </para>
    /// </remarks>
    public static decimal? MaxPain(IReadOnlyCollection<StrikeInterest> strikes)
    {
        if (strikes is null || strikes.Count == 0) return null;
        if (strikes.All(x => x.CallOpenInterest <= 0 && x.PutOpenInterest <= 0)) return null;

        decimal? best = null;
        decimal bestPain = decimal.MaxValue;

        foreach (var settlement in strikes.Select(x => x.Strike).Distinct().OrderBy(x => x))
        {
            decimal pain = 0m;

            foreach (var strike in strikes)
            {
                if (settlement > strike.Strike)
                    pain += (settlement - strike.Strike) * strike.CallOpenInterest;

                if (settlement < strike.Strike)
                    pain += (strike.Strike - settlement) * strike.PutOpenInterest;
            }

            if (pain < bestPain)
            {
                bestPain = pain;
                best = settlement;
            }
        }

        return best;
    }

    /// <summary>
    /// The strike carrying the most open interest — where the market has written
    /// the most contracts, and so where it is most heavily defended. Given a
    /// spot, only its own side counts: calls at or above it (resistance), puts
    /// at or below it (support). A deep in-the-money wall left from an older
    /// price is not a level the market has to cross, and without the rule one
    /// heavy strike 2,000 points away read as both support and resistance.
    /// No strike on that side with open interest is null, never the far side.
    /// </summary>
    public static decimal? HeaviestStrike(IReadOnlyCollection<StrikeInterest> strikes, string optionType, decimal spot = 0m)
    {
        if (strikes is null || strikes.Count == 0) return null;

        bool calls = string.Equals(optionType, "CE", StringComparison.OrdinalIgnoreCase);

        var heaviest = strikes
            .Where(x => spot <= 0 || (calls ? x.Strike >= spot : x.Strike <= spot))
            .Where(x => (calls ? x.CallOpenInterest : x.PutOpenInterest) > 0)
            .OrderByDescending(x => calls ? x.CallOpenInterest : x.PutOpenInterest)
            // A tie goes to the strike nearer the spot, the one price meets
            // first; ascending strike alone would pick the farthest put.
            .ThenBy(x => Math.Abs(x.Strike - spot))
            .ThenBy(x => x.Strike)
            .Select(x => (decimal?)x.Strike)
            .FirstOrDefault();

        return heaviest;
    }

    /// <summary>
    /// The strike nearest the spot — the row a chain scrolls to and colours.
    /// </summary>
    public static decimal? AtTheMoney(IReadOnlyCollection<decimal> strikes, decimal spot)
    {
        if (strikes is null || strikes.Count == 0) return null;

        return strikes
            .OrderBy(x => Math.Abs(x - spot))
            .ThenBy(x => x)
            .First();
    }
}
