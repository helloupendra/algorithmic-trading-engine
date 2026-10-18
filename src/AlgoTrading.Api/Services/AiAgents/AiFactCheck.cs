using System.Globalization;
using System.Text.RegularExpressions;

namespace AlgoTrading.Api.Services.AiAgents;

/// <summary>
/// The checks that keep an agent to what it was given: a quote must be in the source's text, and a number, a time or
/// a date it writes must be one the code handed it. Plain code, so a figure the model made up cannot pass.
/// </summary>
/// <remarks>
/// <para>
/// A number matches when the text it was given holds the same value, its sign, rupee mark, Indian or Western digit
/// grouping and a per cent sign aside, or holds a value that rounds to it at the decimals it was written with: "75%"
/// passes for "75.4%", "₹1,18,028" for "−118028". Times (HH:mm) and dates (yyyy-MM-dd) are matched as themselves.
/// </para>
/// <para>
/// The integers 0 to 10 always pass: counts such as "3 ideas" or "2 strategies" are the answer's own structure, and
/// failing a whole report on one would teach nothing. Anything else the model writes in "lakh", "k" or "crore" does
/// not match, since the given text has none of those forms; the prompts ask for numbers as they appear.
/// </para>
/// </remarks>
public static partial class AiFactCheck
{
    /// <summary>Lower case, one space between words, typographic quotes and dashes made plain: how a quote is matched.</summary>
    public static string Normalise(string? text)
    {
        string plain = (text ?? string.Empty).ToLowerInvariant()
            .Replace('‘', '\'').Replace('’', '\'').Replace('“', '"').Replace('”', '"')
            .Replace('–', '-').Replace('—', '-').Replace('−', '-').Replace(' ', ' ');
        return Spaces().Replace(plain, " ").Trim();
    }

    /// <summary>
    /// Whether <paramref name="quote"/> is in <paramref name="source"/>, case, spacing and typographic marks aside, and
    /// long enough to say something (<paramref name="minLength"/> characters once normalised).
    /// </summary>
    public static bool QuoteIn(string? quote, string source, int minLength = 12)
    {
        string q = Normalise(quote).Trim('"', '\'', ' ', '.', '…');
        return q.Length >= minLength && Normalise(source).Contains(q, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="quote"/> is the source's own words: one passage, or several stitched together ("a … b",
    /// "a; b", "a | b", or "a", "b"), each passage of four characters or more found in <paramref name="source"/> word for
    /// word, and <paramref name="minLength"/> characters between them. Case, typographic marks and all whitespace are
    /// set aside, so a word a PDF's text split in two ("de termination") still matches the whole word the model wrote.
    /// </summary>
    /// <remarks>
    /// The Validator's REJECTs taught this on 11 Oct 2026 (40 of 60 quoted the brief in pieces); a circular with
    /// several changes is quoted the same way. Nothing the source does not hold can pass: every piece is looked for.
    /// </remarks>
    public static bool QuotePassagesIn(string? quote, string source, int minLength = 12)
    {
        string text = Compact(source);
        string whole = Normalise(quote).Trim('"', '\'', ' ', '.', '…');
        if (whole.Length >= minLength && Compact(whole).Length > 0 && text.Contains(Compact(whole), StringComparison.Ordinal)) return true;

        var parts = QuoteJoins().Split(Normalise(quote))
            .Select(p => p.Trim().Trim('"', '\'', ',', ';', '.', '…', ' ').Trim())
            .Where(p => Compact(p).Length >= 4)
            .ToList();
        return parts.Count > 0 && parts.Sum(p => p.Length) >= minLength && parts.All(p => text.Contains(Compact(p), StringComparison.Ordinal));
    }

    /// <summary><see cref="Normalise"/> with every space taken out: how a passage is looked for.</summary>
    private static string Compact(string? text) => Spaces().Replace(Normalise(text), string.Empty);

    /// <summary>
    /// The numbers, times and dates in <paramref name="written"/> that <paramref name="given"/> does not hold, as
    /// written, in order of first appearance; empty when every one is supported.
    /// </summary>
    public static IReadOnlyList<string> Unsupported(string? written, string given)
    {
        var have = Read(given);
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in Tokens(written ?? string.Empty))
        {
            if (!seen.Add(token.Raw)) continue;
            bool ok = token.Kind switch
            {
                TokenKind.Date => have.Dates.Contains(token.Key),
                TokenKind.Time => have.Times.Contains(token.Key),
                _ => token.Value is >= 0 and <= 10 && token.Decimals == 0 || have.Numbers.Any(h => Matches(token, h)),
            };
            if (!ok) missing.Add(token.Raw);
        }

        return missing;
    }

    private static bool Matches(Token written, decimal given) =>
        written.Value == given || Math.Round(given, written.Decimals, MidpointRounding.AwayFromZero) == written.Value;

    private sealed record Facts(HashSet<string> Dates, HashSet<string> Times, List<decimal> Numbers);

    private static Facts Read(string text)
    {
        var facts = new Facts(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal), []);
        foreach (var t in Tokens(text))
        {
            switch (t.Kind)
            {
                case TokenKind.Date:
                    facts.Dates.Add(t.Key);
                    // A year cited from a date in the tables ("the 2020-23 holdout", "2024-2025") is the table's.
                    facts.Numbers.Add(int.Parse(t.Key[..4], CultureInfo.InvariantCulture));
                    break;
                case TokenKind.Time:
                    facts.Times.Add(t.Key);
                    break;
                default:
                    facts.Numbers.Add(t.Value);
                    break;
            }
        }

        return facts;
    }

    private enum TokenKind
    {
        Number,
        Time,
        Date,
    }

    private sealed record Token(TokenKind Kind, string Raw, string Key, decimal Value, int Decimals);

    /// <summary>The dates, times and numbers in <paramref name="text"/>, in the order they appear.</summary>
    private static IEnumerable<Token> Tokens(string text)
    {
        // Dates and times first, blanked out so their digits are not read again as numbers.
        var found = new List<(int At, Token Token)>();
        var chars = text.ToCharArray();
        foreach (Match m in DateRx().Matches(text))
        {
            found.Add((m.Index, new Token(TokenKind.Date, m.Value, m.Value, 0, 0)));
            Blank(chars, m);
        }

        string rest = new(chars);
        foreach (Match m in TimeRx().Matches(rest))
        {
            int h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            found.Add((m.Index, new Token(TokenKind.Time, m.Value, $"{h:00}:{m.Groups["m"].Value}", 0, 0)));
            Blank(chars, m);
        }

        // A span of years ("the 2024-25 table", "2020-23") is two years, not a year and the number 25.
        rest = new string(chars);
        foreach (Match m in YearSpanRx().Matches(rest))
        {
            int from = int.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture);
            int to = from / 100 * 100 + int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture);
            if (to <= from) continue;
            found.Add((m.Index, new Token(TokenKind.Number, m.Groups["a"].Value, m.Groups["a"].Value, from, 0)));
            found.Add((m.Index + 1, new Token(TokenKind.Number, m.Value, $"{to}", to, 0)));
            Blank(chars, m);
        }

        rest = new string(chars);
        foreach (Match m in NumberRx().Matches(rest))
        {
            string digits = m.Groups["n"].Value.Replace(",", string.Empty, StringComparison.Ordinal);
            if (!decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal value)) continue;
            int dot = digits.IndexOf('.', StringComparison.Ordinal);
            int decimals = dot < 0 ? 0 : digits.Length - dot - 1;
            found.Add((m.Index, new Token(TokenKind.Number, m.Value.Trim(), digits, value, decimals)));
        }

        return found.OrderBy(f => f.At).Select(f => f.Token);
    }

    private static void Blank(char[] chars, Match m)
    {
        for (int i = m.Index; i < m.Index + m.Length; i++) chars[i] = ' ';
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    // How a model stitches passages into one quote: an ellipsis, a semicolon or a bar between them, or quoted pieces
    // separated by commas.
    [GeneratedRegex(@"\.\.\.|…|\s\|\s|;\s|""\s*,\s*""")]
    private static partial Regex QuoteJoins();

    [GeneratedRegex(@"(?<!\d)\d{4}-\d{2}-\d{2}(?!\d)")]
    private static partial Regex DateRx();

    [GeneratedRegex(@"(?<![\d:])(?<h>[01]?\d|2[0-3]):(?<m>[0-5]\d)(?![\d:])")]
    private static partial Regex TimeRx();

    [GeneratedRegex(@"(?<![\d.,-])(?<a>(?:19|20)\d{2})\s*[-–]\s*(?<b>\d{2})(?![\d,-]|\.\d)")]
    private static partial Regex YearSpanRx();

    // A number that stands on its own: not inside a word or a symbol (NIFTY50, INDIA VIX's "NSE:INDIAVIX"), digits
    // with optional Indian or Western grouping and decimals. The sign and the rupee mark are not part of the value.
    [GeneratedRegex(@"(?<![\p{L}\d.,])(?<n>\d{1,3}(?:,\d{2,3})+(?:\.\d+)?|\d+(?:\.\d+)?)(?![\p{L}\d])")]
    private static partial Regex NumberRx();
}
