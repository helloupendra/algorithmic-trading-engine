using System.Globalization;
using System.IO.Compression;
using System.Xml;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services.MutualFunds.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>
/// Keeps <c>mf_ter</c> current from AMFI's TER disclosure: every scheme's total expense ratio for each day of a
/// month, the regular and the direct plan side by side, as the Excel file AMFI's TER page downloads
/// (<c>/api/populate-te-rdata-revised?MF_ID=All&amp;Month=10-2026&amp;strCat=-1&amp;strType=-1&amp;excel=true</c>).
/// </summary>
/// <remarks>
/// One request a month: about 4.4 MB, 62,000 rows, 20–30 s for AMFI to produce (1 Nov 2026). The nightly run reads
/// the current month, and in the first five days of a month the month before too, so the last days of a month are
/// never missed. The page's JSON form serves 100 rows a request (625 requests a month), so the Excel form is used.
/// The columns changed with SEBI's 2026 TER rules (base expense ratio, brokerage, transaction cost, statutory levies
/// in place of base TER, 6A(b), 6A(c), GST); only the scheme and the two plans' total TER are kept, found by their
/// headers, so either layout reads.
/// </remarks>
public sealed class MutualFundTerSync
{
    /// <summary>
    /// Fewer rows than this for a finished month is a broken file (one day of every scheme is about 2,170). The
    /// current month has no floor: on 1 Nov 2026 AMFI's November file held 361 rows, the TERs announced ahead.
    /// </summary>
    public const int MinimumRows = 500;

    private readonly TradingDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly MutualFundsOptions _options;
    private readonly ILogger<MutualFundTerSync> _logger;

    public MutualFundTerSync(TradingDbContext db, IHttpClientFactory http, IOptions<MutualFundsOptions> options, ILogger<MutualFundTerSync> logger)
    {
        _db = db;
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The months a run reads on <paramref name="today"/>: this one, and the one before during the first five days.</summary>
    public static IReadOnlyList<DateOnly> MonthsFor(DateOnly today)
    {
        var month = new DateOnly(today.Year, today.Month, 1);
        return today.Day <= 5 ? [month.AddMonths(-1), month] : [month];
    }

    public static string MonthParameter(DateOnly month) => month.ToString("MM-yyyy", CultureInfo.InvariantCulture);

    /// <summary>Reads the month(s) due and stores them. Never throws for AMFI's or the database's faults.</summary>
    public async Task<MutualFundJobRun> RunAsync(string trigger, CancellationToken ct)
    {
        var started = Clock();
        int written = 0, read = 0;
        DateOnly? reached = null;
        var errors = new List<string>();
        var today = IstTime.DateOf(started);
        foreach (var month in MonthsFor(today))
        {
            try
            {
                var rows = await FetchAsync(month, finished: month.AddMonths(1) <= today, ct);
                read += rows.Count;
                written += await StoreAsync(rows, ct);
                if (rows.Count > 0)
                {
                    var last = rows.Max(r => r.Date);
                    if (reached is null || last > reached) reached = last;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                errors.Add($"{month:MMM yyyy}: {ex.Message}");
                _logger.LogWarning("TER sync: {Month:MMM yyyy} failed: {Error}", month, ex.Message);
            }
        }

        string message = $"{read:N0} TER rows read, {written:N0} written"
                         + (reached is null ? string.Empty : $"; TER to {FundAnalytics.Day(reached.Value)}")
                         + (errors.Count > 0 ? ". Failed: " + string.Join("; ", errors) : ".");
        var run = new MutualFundJobRun
        {
            Trigger = trigger,
            StartedUtc = started,
            FinishedUtc = Clock(),
            Ok = errors.Count == 0,
            Message = MutualFundAnalyticsSettings.Clip(message),
            Reached = reached,
            Rows = written,
        };
        _logger.LogInformation("TER sync ({Trigger}): {Message}", trigger, run.Message);
        return run;
    }

    private async Task<List<MutualFundTer>> FetchAsync(DateOnly month, bool finished, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        string url = $"{_options.TerUrl}&Month={MonthParameter(month)}";
        using var response = await _http.CreateClient(MutualFundsOptions.HttpClientName).GetAsync(url, deadline.Token);
        response.EnsureSuccessStatusCode();
        await using var buffer = new MemoryStream();
        await response.Content.CopyToAsync(buffer, deadline.Token);
        buffer.Position = 0;
        var rows = Parse(buffer);
        if (finished && rows.Count < MinimumRows)
            throw new InvalidDataException($"AMFI's TER file for {month:MMM yyyy} had {rows.Count:N0} rows, fewer than the {MinimumRows:N0} expected; nothing was stored.");
        return rows;
    }

    /// <summary>
    /// Reads AMFI's TER workbook: the first sheet, a header row, one row per scheme and day. Columns are found by
    /// their headers; footnote rows (no NSDL code) are skipped.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a workbook, or the headers are not AMFI's.</exception>
    public static List<MutualFundTer> Parse(Stream workbook)
    {
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(workbook, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("AMFI's TER answer is not an Excel workbook.");
        }

        using (zip)
        {
            var shared = SharedStrings(zip);
            var sheet = zip.GetEntry("xl/worksheets/sheet1.xml")
                        ?? zip.Entries.FirstOrDefault(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                        ?? throw new InvalidDataException("AMFI's TER workbook has no worksheet.");

            var result = new List<MutualFundTer>();
            Dictionary<string, int>? columns = null;
            using var stream = sheet.Open();
            foreach (var row in Rows(stream, shared))
            {
                if (columns is null)
                {
                    columns = Header(row);
                    continue;
                }

                if (ToTer(row, columns) is { } ter) result.Add(ter);
            }

            if (columns is null) throw new InvalidDataException("AMFI's TER workbook is empty.");
            return result;
        }
    }

    /// <summary>The header row's columns the parser needs, by what their headers say.</summary>
    private static Dictionary<string, int>? Header(Dictionary<int, string> row)
    {
        int Find(Func<string, bool> match) => row.Where(c => match(c.Value.ToLowerInvariant())).Select(c => c.Key).DefaultIfEmpty(-1).First();
        var columns = new Dictionary<string, int>
        {
            ["code"] = Find(h => h.Contains("nsdl", StringComparison.Ordinal)),
            ["name"] = Find(h => h.Contains("scheme name", StringComparison.Ordinal)),
            ["type"] = Find(h => h.Contains("scheme type", StringComparison.Ordinal)),
            ["category"] = Find(h => h.Contains("scheme category", StringComparison.Ordinal)),
            ["date"] = Find(h => h.Contains("ter date", StringComparison.Ordinal)),
            ["regular"] = Find(h => h.StartsWith("regular", StringComparison.Ordinal) && h.Contains("total ter", StringComparison.Ordinal)),
            ["direct"] = Find(h => h.StartsWith("direct", StringComparison.Ordinal) && h.Contains("total ter", StringComparison.Ordinal)),
        };

        var missing = columns.Where(c => c.Value < 0 && c.Key is "code" or "name" or "date").Select(c => c.Key).ToList();
        if (missing.Count > 0 || (columns["regular"] < 0 && columns["direct"] < 0))
            throw new InvalidDataException("AMFI's TER workbook does not have the expected headers (NSDL Scheme Code, Scheme Name, TER Date, Total TER).");
        return columns;
    }

    private static MutualFundTer? ToTer(Dictionary<int, string> row, Dictionary<string, int> columns)
    {
        string Cell(string key) => columns[key] >= 0 && row.TryGetValue(columns[key], out var v) ? v.Trim() : string.Empty;

        string code = Cell("code");
        string name = Cell("name");
        // Footnotes have no date; a row without either plan's TER says nothing.
        if (code.Length == 0 || name.Length == 0 || ParseDate(Cell("date")) is not { } date) return null;
        decimal? regular = ParsePercent(Cell("regular")), direct = ParsePercent(Cell("direct"));
        if (regular is null && direct is null) return null;

        string nameKey = MutualFundNames.Key(name);
        return new MutualFundTer
        {
            NsdlSchemeCode = Clip(TerKey(code, nameKey), 120),
            Date = date,
            SchemeName = Clip(name, 300),
            NameKey = Clip(nameKey, 300),
            SchemeType = Clip(Cell("type"), 40),
            Category = Clip(Cell("category"), 200),
            RegularTer = regular,
            DirectTer = direct,
        };
    }

    /// <summary>
    /// The row's key: AMFI's NSDL code, or, where AMFI writes "NSDL Code not allocated" (several schemes share those
    /// words), "name:" and the scheme's name key, so two such schemes never overwrite each other.
    /// </summary>
    public static string TerKey(string code, string nameKey) =>
        code.Contains("not allo", StringComparison.OrdinalIgnoreCase) ? "name:" + nameKey : code;

    /// <summary>An Excel serial day (46296 is 1 Oct 2026) or a written date.</summary>
    public static DateOnly? ParseDate(string text)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double serial) && serial is > 20000 and < 80000)
            return DateOnly.FromDateTime(new DateTime(1899, 12, 30).AddDays(Math.Floor(serial)));
        string[] formats = ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-ddTHH:mm:ss", "dd-MM-yyyy", "dd-MMM-yyyy", "dd/MM/yyyy", "d MMM yyyy"];
        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? DateOnly.FromDateTime(parsed)
            : null;
    }

    /// <summary>A TER cell in percent; blank, "-" or a negative value is not known.</summary>
    private static decimal? ParsePercent(string text) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and < 100
            ? Math.Round(value, 4)
            : null;

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var result = new List<string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return result;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = false, DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read())
        {
            if (reader is { NodeType: XmlNodeType.Element, LocalName: "si" })
                result.Add(Text(reader.ReadSubtree()));
        }

        return result;
    }

    /// <summary>Each row of a sheet as column index → text, in order; empty rows are skipped.</summary>
    private static IEnumerable<Dictionary<int, string>> Rows(Stream sheet, List<string> shared)
    {
        using var reader = XmlReader.Create(sheet, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        while (reader.Read())
        {
            if (reader is not { NodeType: XmlNodeType.Element, LocalName: "row" }) continue;
            var row = new Dictionary<int, string>();
            using var cells = reader.ReadSubtree();
            while (cells.Read())
            {
                if (cells is not { NodeType: XmlNodeType.Element, LocalName: "c" }) continue;
                int column = ColumnIndex(cells.GetAttribute("r"));
                string? type = cells.GetAttribute("t");
                string value = string.Empty;
                using (var cell = cells.ReadSubtree())
                {
                    while (cell.Read())
                    {
                        if (cell is { NodeType: XmlNodeType.Element, LocalName: "v" }) value = cell.ReadElementContentAsString();
                        else if (cell is { NodeType: XmlNodeType.Element, LocalName: "is" }) value = Text(cell.ReadSubtree());
                    }
                }

                if (type == "s" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int at) && at >= 0 && at < shared.Count) value = shared[at];
                if (column >= 0 && value.Length > 0) row[column] = value;
            }

            if (row.Count > 0) yield return row;
        }
    }

    /// <summary>The text of every &lt;t&gt; inside an element, joined (rich text comes in runs).</summary>
    private static string Text(XmlReader subtree)
    {
        using (subtree)
        {
            var text = new System.Text.StringBuilder();
            while (subtree.Read())
            {
                if (subtree is { NodeType: XmlNodeType.Element, LocalName: "t" }) text.Append(subtree.ReadElementContentAsString());
            }

            return text.ToString();
        }
    }

    /// <summary>"C12" → 2; -1 without a reference.</summary>
    private static int ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return -1;
        int column = 0, i = 0;
        for (; i < reference.Length && char.IsLetter(reference[i]); i++) column = column * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
        return i == 0 ? -1 : column - 1;
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>Upserts TER rows in batches; a day stored with the same values is not rewritten.</summary>
    private async Task<int> StoreAsync(List<MutualFundTer> rows, CancellationToken ct)
    {
        int written = 0;
        var unique = rows.GroupBy(r => (r.NsdlSchemeCode, r.Date)).Select(g => g.Last()).OrderBy(r => r.NsdlSchemeCode, StringComparer.Ordinal).ThenBy(r => r.Date).ToList();
        foreach (var batch in unique.Chunk(MutualFundStore.BatchSize))
        {
            written += await _db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO mf_ter AS t ("NsdlSchemeCode", "Date", "SchemeName", "NameKey", "SchemeType", "Category", "RegularTer", "DirectTer")
                SELECT * FROM unnest(@code, @date, @name, @key, @type, @category, @regular, @direct)
                ON CONFLICT ("NsdlSchemeCode", "Date") DO UPDATE SET
                    "SchemeName" = EXCLUDED."SchemeName", "NameKey" = EXCLUDED."NameKey", "SchemeType" = EXCLUDED."SchemeType",
                    "Category" = EXCLUDED."Category", "RegularTer" = EXCLUDED."RegularTer", "DirectTer" = EXCLUDED."DirectTer"
                WHERE (t."SchemeName", t."RegularTer", t."DirectTer", t."Category") IS DISTINCT FROM
                      (EXCLUDED."SchemeName", EXCLUDED."RegularTer", EXCLUDED."DirectTer", EXCLUDED."Category")
                """,
                [
                    Array("code", NpgsqlDbType.Text, batch.Select(r => r.NsdlSchemeCode).ToArray()),
                    Array("date", NpgsqlDbType.Date, batch.Select(r => r.Date).ToArray()),
                    Array("name", NpgsqlDbType.Text, batch.Select(r => r.SchemeName).ToArray()),
                    Array("key", NpgsqlDbType.Text, batch.Select(r => r.NameKey).ToArray()),
                    Array("type", NpgsqlDbType.Text, batch.Select(r => r.SchemeType).ToArray()),
                    Array("category", NpgsqlDbType.Text, batch.Select(r => r.Category).ToArray()),
                    Array("regular", NpgsqlDbType.Numeric, batch.Select(r => r.RegularTer).ToArray()),
                    Array("direct", NpgsqlDbType.Numeric, batch.Select(r => r.DirectTer).ToArray()),
                ],
                ct);
        }

        return written;
    }

    private static NpgsqlParameter Array<T>(string name, NpgsqlDbType type, T[] values) =>
        new(name, NpgsqlDbType.Array | type) { Value = values };
}
