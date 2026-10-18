using System.Globalization;
using System.Text.RegularExpressions;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>
/// The dates and clock times a circular's text holds, as a regulator or a broker writes them, and the ones a triage
/// wrote that the text does not hold. Plain code: a date or a time the model made up, or worked out for itself, cannot
/// pass the Compliance Triage's check.
/// </summary>
/// <remarks>
/// <para>
/// Dates are read in the forms the sources use: "December 07, 2026" and "Dec 7, 2026" (SEBI), "7th December 2026" and
/// "07-Dec-2026" (NSE, BSE, FYERS), "07/12/2026" and "07.12.2026" (day first, as in India) and "2026-12-07". Two are the
/// same date when they are the same day, whatever the form: the triage's "2026-12-07" is the circular's
/// "December 07, 2026".
/// </para>
/// <para>
/// Times are read as "3:45 p.m.", "3:45 PM", "3.45 pm" (a dot only with a.m. or p.m., or it is a paragraph number),
/// "15:45" and "15:45 hrs". "3:45 p.m." and "15:45" are the same time; a bare "3:45" is either of 03:45 and 15:45, so a
/// timetable that leaves out "p.m." is not held against the triage that puts it in, nor the other way round.
/// </para>
/// </remarks>
public static partial class CircularFacts
{
    /// <summary>The days written in <paramref name="text"/>.</summary>
    public static HashSet<DateOnly> Dates(string? text) => DateTokens(text ?? string.Empty).Select(t => t.Day).ToHashSet();

    /// <summary>
    /// The dates and times in <paramref name="written"/> that <paramref name="source"/> does not hold, as written, in
    /// order of first appearance; empty when every one is there.
    /// </summary>
    public static IReadOnlyList<string> Unsupported(string? written, string source)
    {
        var dates = Dates(source);
        var times = SourceTimes(source);
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (at, raw, ok) in DateTokens(written ?? string.Empty).Select(t => (t.At, t.Raw, dates.Contains(t.Day)))
                     .Concat(TimeTokens(written ?? string.Empty).Select(t => (t.At, t.Raw, Supported(t, times))))
                     .OrderBy(t => t.At))
        {
            if (!ok && seen.Add(raw)) missing.Add(raw);
        }

        return missing;
    }

    /// <summary>A date written as yyyy-MM-dd, or null.</summary>
    public static DateOnly? IsoDate(string? text) =>
        DateOnly.TryParseExact((text ?? string.Empty).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private sealed record DateToken(int At, string Raw, DateOnly Day);

    /// <summary>A clock time: the minute of the day as written (hour, minute), and whether a.m. or p.m. said which half.</summary>
    private sealed record TimeToken(int At, string Raw, int Hour, int Minute, char? Half)
    {
        /// <summary>The minute of the day, a.m. and p.m. taken into account.</summary>
        public int Minutes => (Half switch
        {
            'p' when Hour < 12 => Hour + 12,
            'a' when Hour == 12 => 0,
            _ => Hour,
        }) * 60 + Minute;
    }

    private static bool Supported(TimeToken t, HashSet<int> times) =>
        times.Contains(t.Minutes) || t.Half is null && t.Hour is >= 1 and <= 11 && times.Contains(t.Minutes + 12 * 60);

    /// <summary>Every minute of the day the source's times can mean: a bare "3:30" in a timetable is 03:30 or 15:30.</summary>
    private static HashSet<int> SourceTimes(string source)
    {
        var set = new HashSet<int>();
        foreach (var t in TimeTokens(source))
        {
            set.Add(t.Minutes);
            if (t.Half is null && t.Hour is >= 1 and <= 11) set.Add(t.Minutes + 12 * 60);
        }

        return set;
    }

    private static IEnumerable<DateToken> DateTokens(string text)
    {
        var found = new List<DateToken>();
        var taken = new bool[text.Length];
        void Add(Match m, int y, int mo, int d)
        {
            if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(Math.Clamp(y, 1, 9999), mo) || y is < 1900 or > 2200) return;
            for (int i = m.Index; i < m.Index + m.Length; i++)
            {
                if (taken[i]) return;
            }

            for (int i = m.Index; i < m.Index + m.Length; i++) taken[i] = true;
            found.Add(new DateToken(m.Index, m.Value, new DateOnly(y, mo, d)));
        }

        foreach (Match m in IsoRx().Matches(text)) Add(m, Int(m, "y"), Int(m, "mo"), Int(m, "d"));
        foreach (Match m in MonthFirstRx().Matches(text)) Add(m, Int(m, "y"), Month(m), Int(m, "d"));
        foreach (Match m in DayFirstRx().Matches(text)) Add(m, Int(m, "y"), Month(m), Int(m, "d"));
        foreach (Match m in NumericRx().Matches(text)) Add(m, Int(m, "y"), Int(m, "mo"), Int(m, "d"));
        return found.OrderBy(t => t.At);
    }

    private static IEnumerable<TimeToken> TimeTokens(string text)
    {
        foreach (Match m in TimeRx().Matches(text))
        {
            char? half = m.Groups["half"].Success ? char.ToLowerInvariant(m.Groups["half"].Value[0]) : null;
            // "4.20" is a paragraph or a price; with a.m. or p.m. after it, "3.45 p.m." is a time.
            if (m.Groups["sep"].Value == "." && half is null) continue;
            int hour = Int(m, "h");
            if (half is not null && hour is < 1 or > 12) continue;
            yield return new TimeToken(m.Index, m.Value.Trim(), hour, Int(m, "m"), half);
        }
    }

    private static int Int(Match m, string group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    private static int Month(Match m) => m.Groups["mon"].Value.ToLowerInvariant()[..3] switch
    {
        "jan" => 1, "feb" => 2, "mar" => 3, "apr" => 4, "may" => 5, "jun" => 6,
        "jul" => 7, "aug" => 8, "sep" => 9, "oct" => 10, "nov" => 11, _ => 12,
    };

    private const string MonthName =
        @"(?<mon>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";

    [GeneratedRegex(@"(?<!\d)(?<y>\d{4})-(?<mo>\d{2})-(?<d>\d{2})(?!\d)")]
    private static partial Regex IsoRx();

    [GeneratedRegex(@"\b" + MonthName + @"\.?\s+(?<d>\d{1,2})(?:st|nd|rd|th)?,?\s+(?<y>\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthFirstRx();

    [GeneratedRegex(@"\b(?<d>\d{1,2})(?:st|nd|rd|th)?(?:\s+of)?[\s-]+" + MonthName + @"\.?,?[\s-]+(?<y>\d{4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex DayFirstRx();

    [GeneratedRegex(@"(?<![\d/.-])(?<d>\d{1,2})(?<s>[-/.])(?<mo>\d{1,2})\k<s>(?<y>\d{4})(?![\d/.-])")]
    private static partial Regex NumericRx();

    [GeneratedRegex(@"(?<![\d:.])(?<h>[01]?\d|2[0-3])(?<sep>[:.])(?<m>[0-5]\d)(?![\d:])(?:\s?(?<half>[ap])\.?\s?m\b\.?)?", RegexOptions.IgnoreCase)]
    private static partial Regex TimeRx();
}
