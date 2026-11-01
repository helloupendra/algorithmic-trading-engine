using System.Globalization;
using System.Text.RegularExpressions;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Infrastructure.Services.MutualFunds;

/// <summary>One scheme's line in an AMFI NAV file, cleaned.</summary>
/// <param name="SchemeCode">AMFI's scheme code.</param>
/// <param name="SchemeName">The name, whitespace tidied: the base name in the daily file, the full NAV name in the history.</param>
/// <param name="Amc">The AMC line the scheme sits under.</param>
/// <param name="Category">The section line the scheme sits under, written as the daily file writes it.</param>
/// <param name="IsinGrowth">The growth (or IDCW payout) ISIN; null when AMFI gives none or something else ("-", "Redeemed").</param>
/// <param name="IsinReinvest">The IDCW reinvestment ISIN, if any.</param>
/// <param name="Plan"><see cref="MutualFundPlans"/>.</param>
/// <param name="Option"><see cref="MutualFundOptions"/>.</param>
/// <param name="OptionLabel">AMFI's option text, whitespace tidied; null when blank.</param>
/// <param name="Nav">The NAV, or null when AMFI's value is not a positive number ("N.A.", text, 0).</param>
/// <param name="Date">The NAV's date, or null when unreadable.</param>
public sealed record AmfiNavRow(
    int SchemeCode,
    string SchemeName,
    string Amc,
    string Category,
    string? IsinGrowth,
    string? IsinReinvest,
    string Plan,
    string Option,
    string? OptionLabel,
    decimal? Nav,
    DateOnly? Date);

/// <summary>What a parse read, for the sync's log line and its checks.</summary>
public sealed class AmfiParseCounts
{
    /// <summary>Scheme lines read.</summary>
    public int Rows { get; set; }

    /// <summary>Scheme lines whose NAV or date could not be used: kept for the master, never stored as a NAV.</summary>
    public int RowsWithoutNav { get; set; }

    /// <summary>Lines that were neither a scheme, a section, an AMC nor blank.</summary>
    public int Unreadable { get; set; }
}

/// <summary>
/// AMFI answered with its download form instead of a file: it has no NAVs for
/// that range and scheme type (before April 2006, or no interval fund that
/// week). HTTP 200 either way, so this is how the two are told apart.
/// </summary>
public sealed class AmfiNoDataException(string message) : Exception(message);

/// <summary>
/// Reads AMFI's semicolon-separated NAV files: the daily <c>NAVAll.txt</c> and
/// the NAV history download. Both are a header line, then blocks: a section
/// line ("Open Ended Schemes(Equity Scheme - Large Cap Fund)"), an AMC line
/// ("UTI Mutual Fund") and the AMC's scheme lines. The columns differ between
/// the two files and over the years, so they are found by the header's names.
/// </summary>
/// <remarks>
/// <para>
/// The cleaning is the point. On 1 Nov 2026 the daily file had 14,441 schemes;
/// 5,709 had an empty Plan and 5,715 an empty Option, and the growth option was
/// written "Growth", "Growth Option", "GROWTH", "GROWTH OPTION", "Direct
/// Growth" and more. A blank plan is read from the name ("... - Direct Plan -
/// Growth"); a blank option from the name, and failing that from the ISINs
/// (only an IDCW option has a reinvestment ISIN). What neither says stays
/// unknown: ETFs, and plans from before direct plans existed (2013) such as
/// "Retail Plan" or "Plan B".
/// </para>
/// <para>
/// Rows are yielded as they are read, so a 60 MB history window never sits
/// in memory whole.
/// </para>
/// </remarks>
public static partial class AmfiNavParser
{
    private const string HeaderStart = "Scheme Code";

    /// <summary>Every scheme line of an AMFI NAV file, in file order.</summary>
    /// <exception cref="AmfiNoDataException">AMFI sent its download form: no NAVs for the range.</exception>
    /// <exception cref="FormatException">Not an AMFI NAV file, or one without the columns this needs.</exception>
    public static IEnumerable<AmfiNavRow> Parse(TextReader reader, AmfiParseCounts? counts = null)
    {
        var lines = new AmfiNavLineReader(counts ?? new AmfiParseCounts());
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (lines.Read(line) is { } row) yield return row;
        }

        lines.Finish();
    }

    /// <summary>The scheme line as a row, or null for a line that is not one (it is counted).</summary>
    internal static AmfiNavRow? ReadSchemeLine(string text, Columns columns, string category, string amc, AmfiParseCounts counts)
    {
        var fields = text.Split(';');
        if (fields.Length < columns.Width || !int.TryParse(fields[columns.Code].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int code))
        {
            counts.Unreadable++;
            return null;
        }

        var row = ToRow(fields, columns, code, category, amc);
        counts.Rows++;
        if (row.Nav is null || row.Date is null) counts.RowsWithoutNav++;
        return row;
    }

    /// <summary>A section line's category, written as the daily file writes it, or null for an AMC line.</summary>
    internal static string? SectionOf(string text) =>
        SectionLine().Match(text) is { Success: true } section
            ? $"{Tidy(section.Groups["type"].Value)}({Tidy(section.Groups["category"].Value)})"
            : null;

    private static AmfiNavRow ToRow(string[] fields, Columns columns, int code, string category, string amc)
    {
        string name = Tidy(fields[columns.Name]);
        string? isinGrowth = columns.IsinGrowth >= 0 ? Isin(fields[columns.IsinGrowth]) : null;
        string? isinReinvest = columns.IsinReinvest >= 0 ? Isin(fields[columns.IsinReinvest]) : null;
        string planText = columns.Plan >= 0 ? Tidy(fields[columns.Plan]) : string.Empty;
        string optionText = columns.Option >= 0 ? Tidy(fields[columns.Option]) : string.Empty;

        return new AmfiNavRow(
            code,
            name,
            amc,
            category,
            isinGrowth,
            isinReinvest,
            NormalisePlan(planText, name),
            NormaliseOption(optionText, name, isinReinvest),
            optionText.Length == 0 ? null : optionText,
            ParseNav(fields[columns.Nav]),
            ParseDate(fields[columns.Date]));
    }

    /// <summary>
    /// Direct, regular or unknown: from AMFI's Plan column, else from the name.
    /// "Direct" in a name always means the direct plan; "Regular" can also be
    /// an IDCW frequency ("Direct Plan - Regular Dividend"), so direct is
    /// looked for first.
    /// </summary>
    public static string NormalisePlan(string? plan, string name)
    {
        string text = plan ?? string.Empty;
        if (DirectWord().IsMatch(text)) return MutualFundPlans.Direct;
        if (RegularWord().IsMatch(text)) return MutualFundPlans.Regular;
        if (DirectWord().IsMatch(name)) return MutualFundPlans.Direct;
        if (RegularWord().IsMatch(name)) return MutualFundPlans.Regular;
        return MutualFundPlans.Unknown;
    }

    /// <summary>
    /// Growth, IDCW, other or unknown: from AMFI's Option column, else from the
    /// name, else IDCW when there is a reinvestment ISIN (growth has none).
    /// </summary>
    public static string NormaliseOption(string? option, string name, string? isinReinvest)
    {
        string fromOption = ClassifyOption(option ?? string.Empty);
        if (fromOption != MutualFundOptions.Unknown) return fromOption;
        if (!string.IsNullOrWhiteSpace(option)) return MutualFundOptions.Other;

        // Fund names use the same words: "ICICI Prudential Growth Fund",
        // "Dividend Yield Fund", "Stable Dividend 50 ETF". Those say nothing
        // about the option and are taken out before the name is read.
        string fromName = ClassifyOption(FundNameWords().Replace(name, " "));
        if (fromName != MutualFundOptions.Unknown) return fromName;

        return isinReinvest is null ? MutualFundOptions.Unknown : MutualFundOptions.Idcw;
    }

    private static string ClassifyOption(string text)
    {
        if (text.Length == 0) return MutualFundOptions.Unknown;
        if (UnclaimedOrBonus().IsMatch(text)) return MutualFundOptions.Other;
        if (IdcwWords().IsMatch(text)) return MutualFundOptions.Idcw;
        if (GrowthWords().IsMatch(text)) return MutualFundOptions.Growth;
        return MutualFundOptions.Unknown;
    }

    /// <summary>A positive NAV, or null: "N.A.", blanks, text, zero and negatives are not NAVs.</summary>
    public static decimal? ParseNav(string text) =>
        decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var nav) && nav > 0
            ? nav
            : null;

    /// <summary>AMFI's "30-Oct-2026", or null.</summary>
    public static DateOnly? ParseDate(string text) =>
        DateOnly.TryParseExact(text.Trim(), ["dd-MMM-yyyy", "d-MMM-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>A well-formed ISIN, upper-cased, or null for "-", "Redeemed", blanks and codes that are not ISINs.</summary>
    public static string? Isin(string text)
    {
        string value = text.Trim().ToUpperInvariant();
        return IsinShape().IsMatch(value) ? value : null;
    }

    internal static string Tidy(string text) => Whitespace().Replace(text, " ").Trim();

    /// <summary>The columns of AMFI's header line, the file's first non-blank line.</summary>
    internal static Columns Header(string line)
    {
        string first = line.Trim().TrimStart('\uFEFF');
        if (!first.StartsWith(HeaderStart, StringComparison.OrdinalIgnoreCase))
        {
            if (first.StartsWith('<'))
                throw new AmfiNoDataException("AMFI answered with its download form, not a NAV file: it has no NAVs for this range.");
            throw new FormatException($"Not an AMFI NAV file: it starts \"{Truncate(first, 80)}\".");
        }

        var names = first.Split(';').Select(n => Tidy(n).ToLowerInvariant()).ToList();
        int Find(Func<string, bool> match) => names.FindIndex(n => match(n));

        var columns = new Columns(
            Code: Find(n => n == "scheme code"),
            Name: Find(n => n is "scheme name" or "nav name"),
            IsinGrowth: Find(n => n.StartsWith("isin") && n.Contains("growth")),
            IsinReinvest: Find(n => n.StartsWith("isin") && n.Contains("reinvest")),
            Plan: Find(n => n == "plan"),
            Option: Find(n => n == "option"),
            Nav: Find(n => n == "net asset value"),
            Date: Find(n => n == "date"));

        if (columns.Code < 0 || columns.Name < 0 || columns.Nav < 0 || columns.Date < 0)
            throw new FormatException($"AMFI's header lacks a column this needs: \"{Truncate(first, 160)}\".");

        return columns;
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    internal sealed record Columns(int Code, int Name, int IsinGrowth, int IsinReinvest, int Plan, int Option, int Nav, int Date)
    {
        /// <summary>How many fields a scheme line needs for every column found.</summary>
        public int Width { get; } = new[] { Code, Name, IsinGrowth, IsinReinvest, Plan, Option, Nav, Date }.Max() + 1;
    }

    // "Open Ended Schemes(Equity Scheme - Large Cap Fund)" in the daily file,
    // "Open Ended Schemes ( Equity Scheme - Multi Cap Fund )" in the history.
    [GeneratedRegex(@"^(?<type>.*\bSchemes?)\s*\((?<category>.*)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionLine();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^[A-Z]{2}[A-Z0-9]{9}[0-9]$")]
    private static partial Regex IsinShape();

    [GeneratedRegex(@"\bdirect\b", RegexOptions.IgnoreCase)]
    private static partial Regex DirectWord();

    [GeneratedRegex(@"\bregular\b", RegexOptions.IgnoreCase)]
    private static partial Regex RegularWord();

    [GeneratedRegex(@"\bunclaimed\b|\bbonus\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnclaimedOrBonus();

    [GeneratedRegex(@"\bidcw\b|\bdividend\b|income\s+distribution|\bpayout\b|\breinvest", RegexOptions.IgnoreCase)]
    private static partial Regex IdcwWords();

    [GeneratedRegex(@"\bgrowth\b|\bcumulative\b", RegexOptions.IgnoreCase)]
    private static partial Regex GrowthWords();

    [GeneratedRegex(@"\bgrowth\s+(?:fund|opportunit\w*|companies|leaders)\b|\bdividend\s+(?:yield|leaders|opportunit\w*)\b|\bstable\s+dividend\b", RegexOptions.IgnoreCase)]
    private static partial Regex FundNameWords();
}

/// <summary>
/// Reads an AMFI NAV file one line at a time, for a caller that reads the lines
/// itself: the history import reads them from the network asynchronously, so a
/// stalled download can be cancelled. <see cref="AmfiNavParser.Parse"/> is the
/// same over a <see cref="TextReader"/>.
/// </summary>
public sealed class AmfiNavLineReader
{
    private readonly AmfiParseCounts _counts;
    private AmfiNavParser.Columns? _columns;
    private string _category = string.Empty;
    private string _amc = string.Empty;

    public AmfiNavLineReader(AmfiParseCounts counts) => _counts = counts;

    /// <summary>The row on this line, or null for a header, section, AMC, blank or unreadable line.</summary>
    /// <exception cref="AmfiNoDataException">The first line is AMFI's download form.</exception>
    /// <exception cref="FormatException">The first line is not AMFI's header.</exception>
    public AmfiNavRow? Read(string line)
    {
        string text = line.Trim();
        if (text.Length == 0) return null;

        if (_columns is null)
        {
            _columns = AmfiNavParser.Header(text);
            return null;
        }

        if (!text.Contains(';'))
        {
            if (AmfiNavParser.SectionOf(text) is { } category) _category = category;
            else _amc = AmfiNavParser.Tidy(text);
            return null;
        }

        return AmfiNavParser.ReadSchemeLine(text, _columns, _category, _amc, _counts);
    }

    /// <summary>At the end of the file: one that held nothing but blanks is not a NAV file.</summary>
    /// <exception cref="FormatException">The file was empty.</exception>
    public void Finish()
    {
        if (_columns is null) throw new FormatException("AMFI's answer was empty.");
    }
}
