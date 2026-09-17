using System.Globalization;
using System.Text.Json;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Infrastructure.Services.MarketFactors;

/// <summary>A GIFT Nifty futures price as NSE IX publishes it.</summary>
public sealed record GiftNiftyQuote(
    decimal LastPrice,
    decimal? DayChange,
    decimal? ChangePercent,
    DateOnly Expiry,
    long ContractsTraded,
    DateTime? AsOfUtc);

/// <summary>One global market's latest price from Yahoo Finance's chart endpoint.</summary>
public sealed record GlobalQuote(
    string Symbol,
    string Name,
    string? Currency,
    decimal LastPrice,
    decimal? PreviousClose,
    DateTime? AsOfUtc);

/// <summary>
/// Reads the files and answers the Market factors page is built from. Pure
/// functions over text, so every one is tested against a real answer.
/// </summary>
/// <remarks>
/// Every parser refuses rather than guesses: a header that moved, a date that
/// does not match the one asked for, or a number that does not parse throws
/// <see cref="FormatException"/> with what was wrong, because a page that shows
/// a wrongly-read FII position is worse than one that says the read failed.
/// </remarks>
public static class MarketFactorParsers
{
    private static readonly string[] ParticipantColumns =
    [
        "Client Type", "Future Index Long", "Future Index Short", "Future Stock Long", "Future Stock Short",
        "Option Index Call Long", "Option Index Put Long", "Option Index Call Short", "Option Index Put Short",
        "Option Stock Call Long", "Option Stock Put Long", "Option Stock Call Short", "Option Stock Put Short",
        "Total Long Contracts", "Total Short Contracts",
    ];

    public static readonly string[] ParticipantTypes = ["Client", "DII", "FII", "Pro", "TOTAL"];

    /// <summary>
    /// NSE's participant-wise open interest file for one day.
    /// </summary>
    /// <remarks>
    /// Line 1 is a title naming the date ("... as on Sep 16, 2026"), line 2 the
    /// header, then Client, DII, FII, Pro and TOTAL. The title's date must be
    /// the one asked for: NSE has served a previous day's file under a new name
    /// before, and storing it under the wrong date would double a day.
    /// </remarks>
    public static IReadOnlyList<MarketParticipantOpenInterest> ParseParticipantOpenInterest(string csv, DateOnly expectedDate, string source)
    {
        var lines = csv.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 7) throw new FormatException($"participant OI file has {lines.Length} lines, expected at least 7");

        string title = lines[0].Replace("\"", string.Empty);
        int at = title.IndexOf("as on", StringComparison.OrdinalIgnoreCase);
        if (at < 0) throw new FormatException("participant OI title does not name its date");
        string dateText = title[(at + 5)..].Split(',').Take(2).Aggregate((a, b) => a + "," + b).Trim();
        if (!DateOnly.TryParseExact(dateText, ["MMM d, yyyy", "MMM dd, yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate))
            throw new FormatException($"participant OI title date '{dateText}' does not parse");
        if (fileDate != expectedDate)
            throw new FormatException($"participant OI file is dated {fileDate:yyyy-MM-dd}, expected {expectedDate:yyyy-MM-dd}");

        var header = lines[1].Split(',').Select(h => h.Trim()).ToArray();
        for (int i = 0; i < ParticipantColumns.Length; i++)
        {
            if (i >= header.Length || !string.Equals(header[i], ParticipantColumns[i], StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"participant OI column {i + 1} is '{(i < header.Length ? header[i] : "missing")}', expected '{ParticipantColumns[i]}'");
        }

        var rows = new List<MarketParticipantOpenInterest>();
        foreach (var line in lines.Skip(2))
        {
            var cells = line.Split(',').Select(c => c.Trim()).ToArray();
            if (cells.Length < ParticipantColumns.Length || string.IsNullOrEmpty(cells[0])) continue;
            string type = ParticipantTypes.FirstOrDefault(t => string.Equals(t, cells[0], StringComparison.OrdinalIgnoreCase))
                ?? throw new FormatException($"unknown participant type '{cells[0]}'");
            long N(int i) => long.TryParse(cells[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v
                : throw new FormatException($"participant OI {type} column '{ParticipantColumns[i]}' is '{cells[i]}'");
            rows.Add(new MarketParticipantOpenInterest
            {
                Date = fileDate,
                ClientType = type,
                FutureIndexLong = N(1), FutureIndexShort = N(2), FutureStockLong = N(3), FutureStockShort = N(4),
                OptionIndexCallLong = N(5), OptionIndexPutLong = N(6), OptionIndexCallShort = N(7), OptionIndexPutShort = N(8),
                OptionStockCallLong = N(9), OptionStockPutLong = N(10), OptionStockCallShort = N(11), OptionStockPutShort = N(12),
                TotalLong = N(13), TotalShort = N(14),
                Source = source,
                FetchedUtc = DateTime.UtcNow,
            });
        }

        if (rows.Count != ParticipantTypes.Length)
            throw new FormatException($"participant OI file has {rows.Count} rows, expected {ParticipantTypes.Length}");

        // Every contract has a buyer and a seller: the TOTAL row's longs equal its shorts.
        var total = rows.Single(r => r.ClientType == "TOTAL");
        if (total.FutureIndexLong != total.FutureIndexShort || total.TotalLong != total.TotalShort)
            throw new FormatException("participant OI TOTAL row does not balance long against short");

        return rows;
    }

    /// <summary>NSE's FII/DII cash-market figures (the latest day it has).</summary>
    public static IReadOnlyList<MarketCashFlow> ParseCashFlows(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("FII/DII answer is not a list");

        var rows = new List<MarketCashFlow>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            string category = Text(item, "category");
            string dateText = Text(item, "date");
            if (!DateOnly.TryParseExact(dateText, "dd-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new FormatException($"FII/DII date '{dateText}' does not parse");
            rows.Add(new MarketCashFlow
            {
                Date = date,
                Category = category,
                BuyValueCrore = Number(item, "buyValue"),
                SellValueCrore = Number(item, "sellValue"),
                NetValueCrore = Number(item, "netValue"),
                Source = source,
                FetchedUtc = DateTime.UtcNow,
            });
        }

        if (rows.Count == 0) throw new FormatException("FII/DII answer is empty");
        return rows;
    }

    /// <summary>
    /// The futures rows (index and stock) of NSE's F&amp;O bhavcopy in the UDiFF format.
    /// </summary>
    /// <remarks>Columns are found by name, not position, so an added column does not shift a read.</remarks>
    public static IReadOnlyList<MarketFuturesDaily> ParseFuturesBhavcopy(string csv, string source)
    {
        using var reader = new StringReader(csv);
        string? headerLine = reader.ReadLine() ?? throw new FormatException("F&O bhavcopy is empty");
        var header = headerLine.Split(',').Select(h => h.Trim()).ToList();
        int Col(string name)
        {
            int i = header.IndexOf(name);
            return i >= 0 ? i : throw new FormatException($"F&O bhavcopy has no '{name}' column");
        }

        int trade = Col("TradDt"), kind = Col("FinInstrmTp"), symbol = Col("TckrSymb"), expiry = Col("XpryDt"),
            close = Col("ClsPric"), prevClose = Col("PrvsClsgPric"), underlying = Col("UndrlygPric"),
            settle = Col("SttlmPric"), oi = Col("OpnIntrst"), oiChange = Col("ChngInOpnIntrst"),
            volume = Col("TtlTradgVol"), turnover = Col("TtlTrfVal");

        var rows = new List<MarketFuturesDaily>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var c = line.Split(',');
            if (c.Length < header.Count) continue;
            string k = c[kind].Trim();
            if (k != "IDF" && k != "STF") continue;

            rows.Add(new MarketFuturesDaily
            {
                Date = DateOnly.ParseExact(c[trade].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Underlying = c[symbol].Trim(),
                InstrumentKind = k,
                ExpiryDate = DateOnly.ParseExact(c[expiry].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Close = Dec(c[close]),
                PreviousClose = Dec(c[prevClose]),
                SettlementPrice = Dec(c[settle]),
                UnderlyingPrice = string.IsNullOrWhiteSpace(c[underlying]) ? null : Dec(c[underlying]),
                OpenInterest = (long)Dec(c[oi]),
                OpenInterestChange = (long)Dec(c[oiChange]),
                Volume = (long)Dec(c[volume]),
                TurnoverValue = Dec(c[turnover]),
                Source = source,
                FetchedUtc = DateTime.UtcNow,
            });
        }

        if (rows.Count == 0) throw new FormatException("F&O bhavcopy has no futures rows");
        return rows;
    }

    /// <summary>
    /// The nearest NIFTY futures contract from NSE IX's derivatives market-rate
    /// answer: the GIFT Nifty price desks quote before the Indian open.
    /// </summary>
    public static GiftNiftyQuote? ParseGiftNifty(string json, DateOnly today)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;

        GiftNiftyQuote? best = null;
        foreach (var item in data.EnumerateArray())
        {
            if (!string.Equals(Text(item, "SYMBOL"), "NIFTY", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(Text(item, "INSTRUMENTTYPE"), "FUTIDX", StringComparison.OrdinalIgnoreCase)) continue;
            if (!DateOnly.TryParseExact(Text(item, "EXPIRYDATE"), "dd-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)) continue;
            if (expiry < today) continue;

            decimal? last = TryNumber(item, "LASTPRICE");
            if (last is not > 0) continue;

            DateTime? asOf = null;
            if (DateTime.TryParseExact(Text(item, "TIMESTMP"), "dd-MMM-yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ist))
                asOf = DateTime.SpecifyKind(ist - IstTime.Offset, DateTimeKind.Utc);

            var quote = new GiftNiftyQuote(last.Value, TryNumber(item, "DAYCHANGE"), TryNumber(item, "PERCHANGE"), expiry,
                item.TryGetProperty("CONTRACTSTRADED", out var ct) && ct.TryGetInt64(out var n) ? n : 0, asOf);
            if (best is null || expiry < best.Expiry) best = quote;
        }

        return best;
    }

    /// <summary>The latest price and previous close from Yahoo Finance's v8 chart answer.</summary>
    public static GlobalQuote? ParseYahooChart(string json, string name)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("chart", out var chart)) return null;
        if (!chart.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0) return null;
        var meta = result[0].GetProperty("meta");

        decimal? price = TryNumber(meta, "regularMarketPrice");
        if (price is null) return null;
        decimal? previous = TryNumber(meta, "chartPreviousClose") ?? TryNumber(meta, "previousClose");
        DateTime? asOf = meta.TryGetProperty("regularMarketTime", out var t) && t.TryGetInt64(out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime
            : null;

        return new GlobalQuote(Text(meta, "symbol"), name, meta.TryGetProperty("currency", out var cur) ? cur.GetString() : null,
            price.Value, previous, asOf);
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : string.Empty;

    private static decimal Number(JsonElement item, string name) =>
        TryNumber(item, name) ?? throw new FormatException($"'{name}' is missing or not a number");

    private static decimal? TryNumber(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    private static decimal Dec(string text) =>
        decimal.TryParse(text.Trim(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var v)
            ? v
            : string.IsNullOrWhiteSpace(text) ? 0m : throw new FormatException($"'{text}' is not a number");
}
