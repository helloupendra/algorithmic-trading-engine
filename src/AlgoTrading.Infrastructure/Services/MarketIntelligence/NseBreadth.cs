using System.Globalization;
using System.IO.Compression;
using System.Net;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services.MarketIntelligence;

/// <summary>One EQ-series line of a cash-market bhavcopy: what breadth needs of it.</summary>
public sealed record CmEquityRow(string Symbol, decimal High, decimal Low, decimal Close, decimal PreviousClose, decimal Volume, decimal TurnoverRupees);

/// <summary>A cash-market bhavcopy's EQ series, and which format it came in.</summary>
public sealed record CmBhavcopy(DateOnly Date, string Format, IReadOnlyList<CmEquityRow> Equities);

/// <summary>
/// Reads NSE's cash-market bhavcopy (both formats) and its adjusted 52-week
/// high/low file, and counts a day's breadth from them. Pure functions over
/// text, tested against real files.
/// </summary>
/// <remarks>
/// Every parser refuses rather than guesses, like <see cref="MarketFactors.MarketFactorParsers"/>:
/// a missing column, a file dated another day or a file too small to be a
/// session throws <see cref="FormatException"/>. A breadth row built from a
/// wrongly-read file would be indistinguishable from a real one.
/// </remarks>
public static class NseBreadthParsers
{
    public const string UdiffFormat = "nse-cm-udiff";
    public const string LegacyFormat = "nse-cm-legacy";

    /// <summary>A real session has thousands of EQ lines; fewer than this is a stub or a broken file.</summary>
    public const int MinimumEquities = 500;

    /// <param name="csv">The file's text.</param>
    /// <param name="minimumEquities">Fewer EQ lines than this is refused; the tests' trimmed samples lower it.</param>
    public static CmBhavcopy ParseCmBhavcopy(string csv, int minimumEquities = MinimumEquities)
    {
        using var reader = new StringReader(csv);
        string headerLine = reader.ReadLine() ?? throw new FormatException("CM bhavcopy is empty");
        var header = Csv.Split(headerLine).Select(h => h.Trim()).ToList();
        int Col(string name) => header.IndexOf(name) is var i and >= 0 ? i : throw new FormatException($"CM bhavcopy has no '{name}' column");

        bool udiff = header.Contains("TradDt");
        string format = udiff ? UdiffFormat : LegacyFormat;
        int date = Col(udiff ? "TradDt" : "TIMESTAMP"), symbol = Col(udiff ? "TckrSymb" : "SYMBOL"), series = Col(udiff ? "SctySrs" : "SERIES"),
            high = Col(udiff ? "HghPric" : "HIGH"), low = Col(udiff ? "LwPric" : "LOW"), close = Col(udiff ? "ClsPric" : "CLOSE"),
            previous = Col(udiff ? "PrvsClsgPric" : "PREVCLOSE"), volume = Col(udiff ? "TtlTradgVol" : "TOTTRDQTY"),
            turnover = Col(udiff ? "TtlTrfVal" : "TOTTRDVAL");
        int segment = udiff ? Col("Sgmt") : -1;
        string[] dateFormats = udiff ? ["yyyy-MM-dd"] : ["dd-MMM-yyyy", "d-MMM-yyyy"];

        var rows = new List<CmEquityRow>();
        var dates = new HashSet<DateOnly>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var c = Csv.Split(line);
            if (c.Count < header.Count - 1) continue;
            if (segment >= 0 && c[segment].Trim() != "CM") continue;
            if (!string.Equals(c[series].Trim(), "EQ", StringComparison.Ordinal)) continue;

            string dateText = c[date].Trim();
            if (!DateOnly.TryParseExact(dateText, dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                throw new FormatException($"CM bhavcopy date '{dateText}' does not parse");
            dates.Add(day);

            rows.Add(new CmEquityRow(c[symbol].Trim(), Dec(c[high]), Dec(c[low]), Dec(c[close]), Dec(c[previous]), Dec(c[volume]), Dec(c[turnover])));
        }

        if (dates.Count != 1) throw new FormatException($"CM bhavcopy holds {dates.Count} dates, expected one");
        if (rows.Count < minimumEquities) throw new FormatException($"CM bhavcopy has only {rows.Count} EQ lines");
        return new CmBhavcopy(dates.Single(), format, rows);
    }

    /// <summary>
    /// NSE's adjusted 52-week high and low per EQ symbol, from the file
    /// effective for <paramref name="expectedDate"/>: the 52 weeks before that
    /// session, adjusted for bonuses, splits and rights. "-" (no trade in the
    /// window) reads as null.
    /// </summary>
    /// <remarks>
    /// The file opens with a disclaimer and an "Effective for 25-Sep-2026"
    /// line; the header's spelling changed over the years ("Adjusted
    /// 52_Week_High" in 2020, "Adjusted_52_Week_High" now), so columns are read
    /// by position after the SYMBOL, SERIES header, which never moved.
    /// </remarks>
    public static IReadOnlyDictionary<string, (decimal? High, decimal? Low)> Parse52Week(string csv, DateOnly expectedDate, int minimumEquities = MinimumEquities)
    {
        var lines = csv.Replace("\r", string.Empty).Split('\n');
        var effective = lines.Select(l => l.Replace("\"", string.Empty).Trim())
            .FirstOrDefault(l => l.StartsWith("Effective for", StringComparison.OrdinalIgnoreCase))
            ?? throw new FormatException("52-week file does not say which day it is for");
        string dateText = effective["Effective for".Length..].Trim().TrimEnd(',');
        if (!DateOnly.TryParseExact(dateText, ["dd-MMM-yyyy", "d-MMM-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate))
            throw new FormatException($"52-week file date '{dateText}' does not parse");
        if (fileDate != expectedDate)
            throw new FormatException($"52-week file is for {fileDate:yyyy-MM-dd}, expected {expectedDate:yyyy-MM-dd}");

        int headerAt = Array.FindIndex(lines, l => l.Replace("\"", string.Empty).StartsWith("SYMBOL,SERIES", StringComparison.OrdinalIgnoreCase));
        if (headerAt < 0) throw new FormatException("52-week file has no SYMBOL,SERIES header");

        var result = new Dictionary<string, (decimal?, decimal?)>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(headerAt + 1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var c = Csv.Split(line);
            if (c.Count < 6 || c[1].Trim() != "EQ") continue;
            result[c[0].Trim()] = (Optional(c[2]), Optional(c[4]));
        }

        if (result.Count < minimumEquities) throw new FormatException($"52-week file has only {result.Count} EQ lines");
        return result;
    }

    /// <summary>
    /// The day's breadth. Advancing, declining and unchanged compare each EQ
    /// line's close with its previous close; the 52-week counts need
    /// <paramref name="week52"/> and are null without it.
    /// </summary>
    public static MarketBreadthDaily Compute(CmBhavcopy bhavcopy, IReadOnlyDictionary<string, (decimal? High, decimal? Low)>? week52)
    {
        var traded = bhavcopy.Equities.Where(r => r.Volume > 0).ToList();
        int? highs = null, lows = null;
        if (week52 is not null)
        {
            highs = traded.Count(r => week52.TryGetValue(r.Symbol, out var w) && w.High is decimal h && r.High > h);
            lows = traded.Count(r => week52.TryGetValue(r.Symbol, out var w) && w.Low is decimal l && r.Low < l);
        }

        return new MarketBreadthDaily
        {
            Exchange = "NSE",
            Date = bhavcopy.Date,
            Advances = traded.Count(r => r.Close > r.PreviousClose),
            Declines = traded.Count(r => r.Close < r.PreviousClose),
            Unchanged = traded.Count(r => r.Close == r.PreviousClose),
            Traded = traded.Count,
            TurnoverCr = Math.Round(traded.Sum(r => r.TurnoverRupees) / 10_000_000m, 2),
            Highs52w = highs,
            Lows52w = lows,
            Source = bhavcopy.Format,
        };
    }

    private static decimal Dec(string text) =>
        decimal.TryParse(text.Trim(), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var v)
            ? v
            : string.IsNullOrWhiteSpace(text) ? 0m : throw new FormatException($"'{text}' is not a number");

    private static decimal? Optional(string text)
    {
        string t = text.Trim();
        return t is "" or "-" ? null : Dec(t);
    }
}

/// <summary>A minimal CSV splitter: commas, and double quotes around a field (with "" inside one).</summary>
internal static class Csv
{
    public static List<string> Split(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cells.Add(cell.ToString()); cell.Clear(); }
            else cell.Append(ch);
        }

        cells.Add(cell.ToString());
        return cells;
    }
}

/// <summary>
/// Fetches one session's breadth from NSE's archives and stores it.
/// </summary>
/// <remarks>
/// <para>
/// Two files per day, both checked on 27 Sep 2026. The bhavcopy comes in the
/// UDiFF format from 8 Jul 2024
/// (<c>nsearchives.nseindia.com/content/cm/BhavCopy_NSE_CM_0_0_0_YYYYMMDD_F_0000.csv.zip</c>,
/// none before) and the older format up to 5 Jul 2024
/// (<c>archives.nseindia.com/content/historical/EQUITIES/YYYY/MON/cmDDMONYYYYbhav.csv.zip</c>,
/// back to 2020 and beyond); the likelier one is asked first and the other
/// only on a 404. The 52-week file
/// (<c>nsearchives.nseindia.com/content/CM_52_wk_High_low_DDMMYYYY.csv</c>)
/// exists from 2020 too; without it the row is still stored, with the
/// 52-week counts null.
/// </para>
/// <para>
/// Both go through <see cref="NseRequestPacer"/>, so the evening run and the
/// backfill together stay at about one request a second.
/// </para>
/// </remarks>
public sealed class BreadthRecorder
{
    /// <summary>The first session published in the UDiFF format.</summary>
    public static readonly DateOnly UdiffFrom = new(2024, 7, 8);

    private static readonly string[] Months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private readonly TradingDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly NseRequestPacer _pacer;
    private readonly ILogger<BreadthRecorder> _logger;

    public BreadthRecorder(TradingDbContext db, IHttpClientFactory http, NseRequestPacer pacer, ILogger<BreadthRecorder> logger)
    {
        _db = db;
        _http = http;
        _pacer = pacer;
        _logger = logger;
    }

    public static string UdiffUrl(DateOnly day) =>
        $"https://nsearchives.nseindia.com/content/cm/BhavCopy_NSE_CM_0_0_0_{day:yyyyMMdd}_F_0000.csv.zip";

    public static string LegacyUrl(DateOnly day)
    {
        string mon = Months[day.Month - 1];
        return $"https://archives.nseindia.com/content/historical/EQUITIES/{day.Year}/{mon}/cm{day.Day:00}{mon}{day.Year}bhav.csv.zip";
    }

    /// <summary>The smallest EQ count accepted as a real session file; lowered in tests only.</summary>
    public int MinimumEquities { get; set; } = NseBreadthParsers.MinimumEquities;

    public static string Week52Url(DateOnly day) =>
        $"https://nsearchives.nseindia.com/content/CM_52_wk_High_low_{day:ddMMyyyy}.csv";

    /// <summary>The dates already stored, for the backfill to skip.</summary>
    public async Task<IReadOnlySet<DateOnly>> DatesPresentAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        (await _db.MarketBreadthDaily.AsNoTracking()
            .Where(x => x.Exchange == "NSE" && x.Date >= from && x.Date <= to)
            .Select(x => x.Date)
            .ToListAsync(ct))
        .ToHashSet();

    public async Task<DayFetchOutcome> FetchDayAsync(DateOnly day, CancellationToken ct)
    {
        try
        {
            string[] urls = day >= UdiffFrom ? [UdiffUrl(day), LegacyUrl(day)] : [LegacyUrl(day), UdiffUrl(day)];
            CmBhavcopy? bhavcopy = null;
            foreach (var url in urls)
            {
                var (status, bytes) = await GetAsync(url, ct);
                if (status == HttpStatusCode.NotFound) continue;
                if (status != HttpStatusCode.OK) return DayFetchOutcome.Failed($"{day:yyyy-MM-dd}: HTTP {(int)status} for {url}");

                bhavcopy = NseBreadthParsers.ParseCmBhavcopy(Unzip(bytes), MinimumEquities);
                break;
            }

            if (bhavcopy is null) return DayFetchOutcome.NotPublished;
            if (bhavcopy.Date != day)
                return DayFetchOutcome.Failed($"{day:yyyy-MM-dd}: the bhavcopy is dated {bhavcopy.Date:yyyy-MM-dd}");

            IReadOnlyDictionary<string, (decimal? High, decimal? Low)>? week52 = null;
            var (weekStatus, weekBytes) = await GetAsync(Week52Url(day), ct);
            if (weekStatus == HttpStatusCode.OK)
            {
                try
                {
                    week52 = NseBreadthParsers.Parse52Week(System.Text.Encoding.UTF8.GetString(weekBytes), day, MinimumEquities);
                }
                catch (FormatException ex)
                {
                    // The day's breadth is still worth storing; only its 52-week counts are unknown.
                    _logger.LogWarning("Breadth {Day}: 52-week file not usable ({Error}); highs and lows left empty.", day, ex.Message);
                }
            }

            var row = NseBreadthParsers.Compute(bhavcopy, week52);
            var existing = await _db.MarketBreadthDaily.FirstOrDefaultAsync(x => x.Exchange == "NSE" && x.Date == day, ct);
            if (existing is null)
            {
                _db.MarketBreadthDaily.Add(row);
            }
            else
            {
                existing.Advances = row.Advances;
                existing.Declines = row.Declines;
                existing.Unchanged = row.Unchanged;
                existing.Traded = row.Traded;
                existing.TurnoverCr = row.TurnoverCr;
                existing.Highs52w = row.Highs52w;
                existing.Lows52w = row.Lows52w;
                existing.Source = row.Source;
            }

            await _db.SaveChangesAsync(ct);
            return DayFetchOutcome.Stored;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _db.ChangeTracker.Clear();
            return DayFetchOutcome.Failed($"{day:yyyy-MM-dd}: {ex.Message}");
        }
    }

    private async Task<(HttpStatusCode, byte[])> GetAsync(string url, CancellationToken ct)
    {
        await _pacer.WaitAsync(ct);
        using var response = await _http.CreateClient(MarketFactors.MarketFactorsSync.HttpClientName).GetAsync(url, ct);
        byte[] body = response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : [];
        return (response.StatusCode, body);
    }

    private static string Unzip(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            ?? throw new FormatException("the zip holds no CSV");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }
}
