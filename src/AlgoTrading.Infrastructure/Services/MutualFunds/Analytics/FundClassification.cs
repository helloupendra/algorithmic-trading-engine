using System.Text;
using System.Text.RegularExpressions;

namespace AlgoTrading.Infrastructure.Services.MutualFunds.Analytics;

/// <summary>AMFI's category line read for comparisons: the scheme type, the SEBI category, and its peer group.</summary>
/// <param name="Raw">As AMFI writes it: "Open Ended Schemes(Equity Scheme - Large Cap Fund)".</param>
/// <param name="Type">"Open Ended", "Close Ended", "Interval Fund", or empty.</param>
/// <param name="Name">The category as shown: "Equity Scheme - Large Cap Fund".</param>
/// <param name="PeerKey">Schemes with the same key are compared with each other; null when the category is from
/// before SEBI's 2017 categories ("Income", "Growth"), which have no peer group.</param>
/// <param name="IndexOrEtf">An index fund or an ETF by its category.</param>
public sealed record FundCategory(string Raw, string Type, string Name, string? PeerKey, bool IndexOrEtf)
{
    /// <summary>"Equity Scheme - Large Cap Fund (open-ended)".</summary>
    public string PeerName => Type.Length == 0 ? Name : $"{Name} ({Type.ToLowerInvariant().Replace(' ', '-')})";
}

/// <summary>
/// Reads AMFI's category lines. AMFI writes one category in several ways: "Equity Scheme - Large Cap Fund" and
/// "Equity Schemes - Large Cap Fund" (SEBI's 2017 names and the renamed sections of 2026) both occur in the same
/// daily file, as do "ELSS" and "ELSS- Tax Saver Fund". Spelling differences are folded into one peer group; debt
/// categories whose 2026 names differ in substance ("Short Duration Fund", "Short Term Fund") are left apart, since
/// the desk cannot tell that their schemes match.
/// </summary>
public static partial class MutualFundCategories
{
    // Same category, different words: a normalised inner name to the one shown.
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["equity scheme - elss"] = "Equity Scheme - ELSS",
        ["equity scheme - elss- tax saver fund"] = "Equity Scheme - ELSS",
        ["equity scheme - elss - tax saver fund"] = "Equity Scheme - ELSS",
        ["equity scheme - sectoral/ thematic"] = "Equity Scheme - Sectoral/Thematic",
        ["equity scheme - sectoral/thematic"] = "Equity Scheme - Sectoral/Thematic",
        ["equity scheme - sectoral fund"] = "Equity Scheme - Sectoral/Thematic",
        ["equity scheme - thematic fund"] = "Equity Scheme - Sectoral/Thematic",
        ["hybrid scheme - dynamic asset allocation or balanced advantage"] = "Hybrid Scheme - Balanced Advantage/Dynamic Asset Allocation",
        ["hybrid scheme - balanced advantage fund/ dynamic asset allocation"] = "Hybrid Scheme - Balanced Advantage/Dynamic Asset Allocation",
        ["hybrid scheme - multi asset allocation"] = "Hybrid Scheme - Multi Asset Allocation",
        ["hybrid scheme - multi asset allocation fund"] = "Hybrid Scheme - Multi Asset Allocation",
        ["hybrid scheme - equity savings"] = "Hybrid Scheme - Equity Savings",
        ["hybrid scheme - equity savings fund"] = "Hybrid Scheme - Equity Savings",
    };

    // The families SEBI's 2017 categorisation (and its 2026 renaming) uses; anything else is an older section.
    private static readonly string[] SebiFamilies =
    [
        "equity scheme", "debt scheme", "hybrid scheme", "solution oriented scheme", "other scheme", "income/debt oriented scheme",
        "index funds", "exchange traded funds", "fund of funds scheme", "life cycle funds", "overseas fund of funds",
    ];

    public static FundCategory Parse(string? raw)
    {
        string text = Tidy(raw ?? string.Empty);
        var m = Section().Match(text);
        string type = m.Success ? m.Groups["type"].Value.Trim() : string.Empty;
        string inner = m.Success ? m.Groups["inner"].Value.Trim() : text;
        inner = inner.Replace("**", string.Empty, StringComparison.Ordinal);
        inner = Spaces().Replace(inner, " ").Trim();

        // "Equity Schemes - X" and "Equity Scheme - X" are one family.
        string family = Plural().Replace(inner, "${word} Scheme");
        string key = family.ToLowerInvariant();
        string name = Synonyms.TryGetValue(key, out var canonical) ? canonical : family;
        bool sebi = SebiFamilies.Any(f => key.StartsWith(f, StringComparison.Ordinal));
        bool indexOrEtf = key.Contains("index fund", StringComparison.Ordinal) || key.Contains("etf", StringComparison.Ordinal);

        string? peerKey = sebi ? $"{type.ToLowerInvariant()}|{name.ToLowerInvariant()}" : null;
        return new FundCategory(raw ?? string.Empty, type, name, peerKey, indexOrEtf);
    }

    private static string Tidy(string text) =>
        Spaces().Replace(text.Replace('’', '\'').Replace('‘', '\''), " ").Trim();

    [GeneratedRegex(@"^(?<type>.*?)\s*Schemes?\s*\((?<inner>.*)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex Section();

    [GeneratedRegex(@"^(?<word>Equity|Debt|Hybrid|Other|Solution Oriented)\s+Schemes\b", RegexOptions.IgnoreCase)]
    private static partial Regex Plural();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

/// <summary>A total-return index the desk collects, by niftyindices.com's name for it.</summary>
/// <param name="Key">niftyindices.com's index name, as stored: "NIFTY 50".</param>
/// <param name="Words">The words a scheme name uses for it, after <see cref="MutualFundNames.Words"/>.</param>
public sealed record BenchmarkIndex(string Key, IReadOnlyList<string> Words)
{
    /// <summary>"NIFTY 50 TRI".</summary>
    public string Name => Key + " TRI";
}

/// <summary>Which benchmark a scheme is compared with, on what basis, and the sentence the report shows about it.</summary>
/// <param name="Basis">"index-name": the index an index fund or ETF names; "category": the category's usual
/// benchmark; "none".</param>
public sealed record BenchmarkChoice(BenchmarkIndex? Index, string Basis, string Note);

/// <summary>
/// The NSE total-return indices the desk collects, and how a scheme is matched to one: an index fund or ETF by the
/// index its name says it tracks, any other equity scheme by its SEBI category's usual benchmark (AMFI's first-tier
/// list of 2021), and nothing for debt, hybrid, sectoral or older categories.
/// </summary>
public static class MutualFundBenchmarks
{
    public static readonly IReadOnlyList<BenchmarkIndex> Indices =
    [
        new("NIFTY 50", ["nifty", "50"]),
        new("NIFTY NEXT 50", ["nifty", "next", "50"]),
        new("NIFTY 100", ["nifty", "100"]),
        new("NIFTY 500", ["nifty", "500"]),
        new("NIFTY MIDCAP 150", ["nifty", "midcap", "150"]),
        new("NIFTY SMALLCAP 250", ["nifty", "smallcap", "250"]),
        new("NIFTY LARGEMIDCAP 250", ["nifty", "largemidcap", "250"]),
        new("NIFTY BANK", ["nifty", "bank"]),
        new("NIFTY500 MULTICAP 50:25:25", ["nifty", "500", "multicap", "50", "25", "25"]),
    ];

    public static BenchmarkIndex? Find(string? key) => Indices.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

    // A category's usual benchmark: AMFI's first-tier benchmarks (SEBI circular of 27 Oct 2021), NSE's of each pair.
    private static readonly Dictionary<string, string> ByCategory = new(StringComparer.Ordinal)
    {
        ["equity scheme - large cap fund"] = "NIFTY 100",
        ["equity scheme - large & mid cap fund"] = "NIFTY LARGEMIDCAP 250",
        ["equity scheme - mid cap fund"] = "NIFTY MIDCAP 150",
        ["equity scheme - small cap fund"] = "NIFTY SMALLCAP 250",
        ["equity scheme - multi cap fund"] = "NIFTY500 MULTICAP 50:25:25",
        ["equity scheme - flexi cap fund"] = "NIFTY 500",
        ["equity scheme - focused fund"] = "NIFTY 500",
        ["equity scheme - value fund"] = "NIFTY 500",
        ["equity scheme - contra fund"] = "NIFTY 500",
        ["equity scheme - dividend yield fund"] = "NIFTY 500",
        ["equity scheme - elss"] = "NIFTY 500",
    };

    // What may follow the index's words in a fund's name: "UTI Nifty 50 Index Fund", "Nippon India ETF Nifty 50 BeES".
    // Anything else ("Nifty 50 Equal Weight", "Nifty 100 Quality 30", "Nifty Midcap 150 Momentum 50") is another index.
    private static readonly HashSet<string> After = new(StringComparer.Ordinal)
    {
        "index", "etf", "fund", "bees", "exchange", "traded", "tri", "plan", "direct", "regular", "growth", "option", "idcw", "fof",
    };

    public static BenchmarkChoice Choose(string schemeName, FundCategory category)
    {
        var words = MutualFundNames.Words(schemeName);
        bool indexFund = category.IndexOrEtf || ContainsRun(words, ["index", "fund"]) || words.Contains("etf");
        if (indexFund)
        {
            foreach (var index in Indices.OrderByDescending(i => i.Words.Count))
            {
                if (Tracks(words, index.Words))
                    return new BenchmarkChoice(index, "index-name",
                        $"{index.Name}: the index the scheme's name says it tracks, with dividends reinvested (total return).");
            }

            return new BenchmarkChoice(null, "none",
                "Benchmark not collected yet: the scheme tracks an index whose total-return values the desk does not collect.");
        }

        if (category.PeerKey is null)
            return new BenchmarkChoice(null, "none", "No benchmark: AMFI lists the scheme under a category from before SEBI's 2017 categories.");

        string name = category.Name.ToLowerInvariant();
        if (ByCategory.TryGetValue(name, out var key) && Find(key) is { } usual)
            return new BenchmarkChoice(usual, "category",
                $"{usual.Name} is the usual benchmark of the category ({category.Name}, AMFI's first-tier benchmark list); the fund's own SID may name another.");

        if (name.StartsWith("equity scheme - sectoral", StringComparison.Ordinal))
            return new BenchmarkChoice(null, "none", "No benchmark: sectoral and thematic funds each follow their own sector or theme index, and none is assigned here.");

        return new BenchmarkChoice(null, "none",
            "Benchmark not collected yet: the desk collects NSE equity total-return indices only; debt and hybrid benchmarks (CRISIL's) are not collected.");
    }

    /// <summary>Whether the index's words appear in a run, followed by nothing or by a word an index fund's name ends with.</summary>
    private static bool Tracks(IReadOnlyList<string> words, IReadOnlyList<string> index)
    {
        for (int at = 0; at + index.Count <= words.Count; at++)
        {
            if (!index.Select((w, k) => words[at + k] == w).All(x => x)) continue;
            int next = at + index.Count;
            if (next == words.Count || After.Contains(words[next])) return true;
        }

        return false;
    }

    private static bool ContainsRun(IReadOnlyList<string> words, IReadOnlyList<string> run)
    {
        for (int at = 0; at + run.Count <= words.Count; at++)
        {
            if (run.Select((w, k) => words[at + k] == w).All(x => x)) return true;
        }

        return false;
    }
}

/// <summary>Scheme names reduced to words, so that two spellings of one name compare equal.</summary>
public static partial class MutualFundNames
{
    /// <summary>
    /// Lower case, "&amp;" as "and", a curly apostrophe as a straight one and then dropped, every other mark a
    /// space, and letters and digits split ("Nifty50" is "nifty 50"): "Axis Children's Fund" and "Axis Childrens
    /// Fund" become "axis childrens fund".
    /// </summary>
    public static string Key(string? name) => string.Join(' ', Words(name));

    public static IReadOnlyList<string> Words(string? name)
    {
        var text = new StringBuilder((name ?? string.Empty).Length + 8);
        foreach (char c in (name ?? string.Empty).ToLowerInvariant())
        {
            if (c is '\'' or '’' or '‘') continue;
            if (c == '&') text.Append(" and ");
            else if (char.IsLetterOrDigit(c)) text.Append(c);
            else text.Append(' ');
        }

        string spaced = LetterDigit().Replace(DigitLetter().Replace(text.ToString(), "$1 $2"), "$1 $2");
        return spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    [GeneratedRegex(@"(\p{L})(\d)")]
    private static partial Regex LetterDigit();

    [GeneratedRegex(@"(\d)(\p{L})")]
    private static partial Regex DigitLetter();
}
