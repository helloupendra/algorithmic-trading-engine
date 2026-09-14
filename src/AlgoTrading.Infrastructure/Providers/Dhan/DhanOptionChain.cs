using System.Globalization;
using System.Text.Json;

namespace AlgoTrading.Infrastructure.Providers.Dhan;

/// <summary>The greeks of one option as Dhan priced them. Null when Dhan sent none.</summary>
public sealed record DhanGreeks(decimal Delta, decimal Theta, decimal Gamma, decimal Vega);

/// <summary>One side of a strike: a call or a put.</summary>
public sealed record DhanChainSide(
    long SecurityId,
    decimal? LastPrice,
    decimal? PreviousClose,
    decimal? AveragePrice,
    decimal? Bid,
    long? BidQuantity,
    decimal? Ask,
    long? AskQuantity,
    long? OpenInterest,
    long? PreviousOpenInterest,
    long? Volume,
    long? PreviousVolume,
    decimal? ImpliedVolatility,
    DhanGreeks? Greeks)
{
    /// <summary>
    /// Change in open interest since the previous close, when both are known.
    /// A level says little; the move says whether writers came in or walked away.
    /// </summary>
    public long? OpenInterestChange =>
        OpenInterest is { } now && PreviousOpenInterest is { } before ? now - before : null;
}

/// <summary>One strike; a side is null when Dhan lists only the other.</summary>
public sealed record DhanChainRow(decimal Strike, DhanChainSide? Call, DhanChainSide? Put);

/// <summary>A whole chain for one underlying and expiry.</summary>
public sealed record DhanOptionChain(
    string Underlying,
    DateOnly Expiry,
    decimal? UnderlyingPrice,
    IReadOnlyList<DhanChainRow> Rows)
{
    /// <summary>The strike carrying the most call open interest, or null when none does.</summary>
    public decimal? PeakCallOiStrike => Rows
        .Where(r => r.Call?.OpenInterest > 0)
        .OrderByDescending(r => r.Call!.OpenInterest)
        .Select(r => (decimal?)r.Strike)
        .FirstOrDefault();

    /// <summary>The strike carrying the most put open interest, or null when none does.</summary>
    public decimal? PeakPutOiStrike => Rows
        .Where(r => r.Put?.OpenInterest > 0)
        .OrderByDescending(r => r.Put!.OpenInterest)
        .Select(r => (decimal?)r.Strike)
        .FirstOrDefault();

    /// <summary>Put open interest over call open interest; null when there is no call OI to divide by.</summary>
    public decimal? PutCallOiRatio
    {
        get
        {
            long calls = Rows.Sum(r => r.Call?.OpenInterest ?? 0);
            long puts = Rows.Sum(r => r.Put?.OpenInterest ?? 0);
            return calls > 0 ? Math.Round((decimal)puts / calls, 4) : null;
        }
    }

    /// <summary>
    /// Reads {"data":{"last_price":…,"oc":{"23950.000000":{"ce":{…},"pe":{…}}}},"status":"success"}.
    /// </summary>
    public static DhanOptionChain Parse(JsonElement root, string underlying, DateOnly expiry)
    {
        var data = root.TryGetProperty("data", out var d) ? d : root;
        decimal? underlyingPrice = Decimal(data, "last_price");

        var rows = new List<DhanChainRow>();
        if (data.TryGetProperty("oc", out var ladder) && ladder.ValueKind == JsonValueKind.Object)
        {
            foreach (var strike in ladder.EnumerateObject())
            {
                if (!decimal.TryParse(strike.Name, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)) continue;

                rows.Add(new DhanChainRow(
                    price,
                    strike.Value.TryGetProperty("ce", out var ce) ? Side(ce) : null,
                    strike.Value.TryGetProperty("pe", out var pe) ? Side(pe) : null));
            }
        }

        return new DhanOptionChain(underlying, expiry, underlyingPrice, rows.OrderBy(r => r.Strike).ToList());
    }

    private static DhanChainSide? Side(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        DhanGreeks? greeks = null;
        if (e.TryGetProperty("greeks", out var g) && g.ValueKind == JsonValueKind.Object)
        {
            decimal delta = Decimal(g, "delta") ?? 0m, theta = Decimal(g, "theta") ?? 0m,
                    gamma = Decimal(g, "gamma") ?? 0m, vega = Decimal(g, "vega") ?? 0m;
            // All four zero is Dhan saying it could not price the option (seen on
            // deep in-the-money puts with no trade), not a real set of greeks.
            if (delta != 0 || theta != 0 || gamma != 0 || vega != 0)
                greeks = new DhanGreeks(delta, theta, gamma, vega);
        }

        decimal? iv = Decimal(e, "implied_volatility");

        return new DhanChainSide(
            SecurityId: Long(e, "security_id") ?? 0,
            LastPrice: Decimal(e, "last_price"),
            PreviousClose: Decimal(e, "previous_close_price"),
            AveragePrice: Decimal(e, "average_price"),
            Bid: Decimal(e, "top_bid_price"),
            BidQuantity: Long(e, "top_bid_quantity"),
            Ask: Decimal(e, "top_ask_price"),
            AskQuantity: Long(e, "top_ask_quantity"),
            OpenInterest: Long(e, "oi"),
            PreviousOpenInterest: Long(e, "previous_oi"),
            Volume: Long(e, "volume"),
            PreviousVolume: Long(e, "previous_volume"),
            // Same reasoning as the greeks: an IV of exactly zero is "not priced".
            ImpliedVolatility: iv is > 0 ? iv : null,
            Greeks: greeks);
    }

    private static decimal? Decimal(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static long? Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        if (v.TryGetInt64(out var l)) return l;
        return v.TryGetDecimal(out var d) ? (long)d : null;
    }
}
