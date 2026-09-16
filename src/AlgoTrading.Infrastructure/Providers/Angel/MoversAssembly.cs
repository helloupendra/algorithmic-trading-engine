using System.Globalization;
using System.Text.Json;

namespace AlgoTrading.Infrastructure.Providers.Angel;

/// <summary>
/// Turning Angel's three answers into the movers screen: pure, so the shapes
/// can be pinned in tests without a network.
/// </summary>
/// <remarks>
/// The vendor's lists are shaped differently from each other — a price list
/// carries <c>ltp</c> and <c>netChange</c>, an OI list carries
/// <c>opnInterest</c> and <c>netChangeOpnInterest</c> and no price at all —
/// and its symbols are futures ("PAYTM29SEP26FUT"), which nobody wants to read
/// in a table.
/// </remarks>
public static class MoversAssembly
{
    /// <summary>"PAYTM29SEP26FUT" -> "PAYTM"; anything unexpected is left alone.</summary>
    public static string Underlying(string tradingSymbol)
    {
        string symbol = (tradingSymbol ?? string.Empty).Trim().ToUpperInvariant();
        int cut = symbol.IndexOf("FUT", StringComparison.Ordinal);
        if (cut <= 0) return symbol;
        string head = symbol[..cut];
        // Strip the expiry that precedes FUT: digits, then a month, then digits.
        for (int i = 0; i < head.Length; i++)
        {
            if (!char.IsDigit(head[i])) continue;
            string candidate = head[..i];
            if (candidate.Length >= 2) return candidate;
            break;
        }
        return head;
    }

    /// <summary>One gainers/losers list. `oiList` says which shape to expect.</summary>
    public static IReadOnlyList<MoverRow> ParseList(JsonElement data, bool oiList)
    {
        var rows = new List<MoverRow>();
        if (data.ValueKind != JsonValueKind.Array) return rows;
        foreach (var item in data.EnumerateArray())
        {
            string symbol = Text(item, "tradingSymbol");
            if (symbol.Length == 0) continue;
            long token = (long)(Number(item, "symbolToken") ?? 0m);
            decimal percent = Number(item, "percentChange") ?? 0m;
            rows.Add(oiList
                ? new MoverRow(symbol, Underlying(symbol), token, Number(item, "ltp") ?? 0m, 0m,
                    Number(item, "opnInterest"), percent)
                : new MoverRow(symbol, Underlying(symbol), token, Number(item, "ltp") ?? 0m, percent));
        }
        return rows;
    }

    public static IReadOnlyList<PcrRow> ParsePcr(JsonElement data)
    {
        var rows = new List<PcrRow>();
        if (data.ValueKind != JsonValueKind.Array) return rows;
        foreach (var item in data.EnumerateArray())
        {
            string symbol = Text(item, "tradingSymbol");
            decimal? pcr = Number(item, "pcr");
            if (symbol.Length == 0 || pcr is null) continue;
            rows.Add(new PcrRow(symbol, Underlying(symbol), pcr.Value));
        }
        return rows;
    }

    /// <summary>What a quote adds to an OI row: the price Angel's OI list leaves out.</summary>
    public readonly record struct QuoteFact(decimal Ltp, decimal PriceChangePercent);

    /// <summary>token -> last price and price change %, from one FULL quote answer.</summary>
    public static IReadOnlyDictionary<long, QuoteFact> ParseQuoteFacts(JsonElement data)
    {
        var map = new Dictionary<long, QuoteFact>();
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("fetched", out var fetched)
            || fetched.ValueKind != JsonValueKind.Array)
        {
            return map;
        }
        foreach (var item in fetched.EnumerateArray())
        {
            decimal? token = Number(item, "symbolToken");
            decimal? percent = Number(item, "percentChange");
            if (token is null || percent is null) continue;
            map[(long)token.Value] = new QuoteFact(Number(item, "ltp") ?? 0m, percent.Value);
        }
        return map;
    }

    /// <summary>
    /// The build-up table: Angel's OI rows, priced from the quote call and
    /// classified. Rows whose price could not be fetched are kept and marked
    /// unclassified rather than dropped, so the count never lies.
    /// </summary>
    public static IReadOnlyList<MoverRow> BuildUp(IEnumerable<MoverRow> oiRows,
        IReadOnlyDictionary<long, QuoteFact> quotes)
    {
        var rows = new List<MoverRow>();
        foreach (var row in oiRows)
        {
            bool priced = quotes.TryGetValue(row.Token, out QuoteFact quote);
            rows.Add(row with
            {
                // Angel's OI list carries no price at all, so both the last
                // price and its change come from the quote call.
                Ltp = priced ? quote.Ltp : row.Ltp,
                PriceChangePercent = priced ? quote.PriceChangePercent : 0m,
                BuildUp = priced ? OiBuildup.Classify(row.OiChangePercent ?? 0m, quote.PriceChangePercent) : null,
            });
        }
        return rows
            .OrderByDescending(r => Math.Abs(r.OiChangePercent ?? 0m))
            .ToList();
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static decimal? Number(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out decimal d) ? d : null,
            JsonValueKind.String => decimal.TryParse(value.GetString(), NumberStyles.Any,
                CultureInfo.InvariantCulture, out decimal parsed) ? parsed : null,
            _ => null,
        };
    }
}
