using System.Text.RegularExpressions;

namespace AlgoTrading.Api.Services.Portfolio;

/// <summary>
/// Refuses advice in a portfolio summary. The page describes a user's holdings; telling them what to do with them is
/// investment advice, which SEBI reserves for registered investment advisers and research analysts.
/// </summary>
/// <remarks>
/// <para>
/// By the words, in English and in Hindi and Hinglish (Latin and Devanagari script): a recommendation to act, the
/// actions themselves (buy, sell, hold, exit, book profit, stop-loss, target, rebalance, accumulate, avoid, trim) and
/// the ways of putting one ("you may want to", "it would be wise"). Strict on purpose: a factual sentence caught by
/// mistake costs one rewrite, an advisory one shown to a user costs the rule.
/// </para>
/// <para>
/// "holding" and "holdings" are not refused: "the largest holding is 34% of value" is the report's own language.
/// "hold" and "holds" are.
/// </para>
/// </remarks>
public static partial class PortfolioAdviceGuard
{
    /// <summary>The advice words in <paramref name="text"/>, as written, in order of first appearance; empty when there are none.</summary>
    public static IReadOnlyList<string> Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<(int At, string Word)>();
        foreach (Match m in English().Matches(text)) found.Add((m.Index, m.Value));
        foreach (Match m in Hinglish().Matches(text)) found.Add((m.Index, m.Value));
        foreach (string stem in Devanagari)
        {
            int at = text.IndexOf(stem, StringComparison.Ordinal);
            if (at >= 0) found.Add((at, stem));
        }

        return found.OrderBy(f => f.At).Select(f => f.Word.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsAdvice(string? text) => Find(text).Count > 0;

    [GeneratedRegex(
        @"\b(" +
        @"should(n't| not)?|ought|consider(s|ed|ing)?|recommend\w*|suggest\w*|advis(e|es|ed|ing|able|ory)|urge\w*|" +
        @"buy(s|ing)?|bought|sell(s|ing)?|sold|hold(s)?|exit\w*|" +
        @"book(s|ed|ing)?\s+(a\s+|some\s+|the\s+)?profits?|profit[-\s]booking|take\s+profits?|" +
        @"stop[-\s]?loss(es)?|targets?|rebalanc\w*|accumulat\w*|avoid\w*|trim(s|med|ming)?|diversify|" +
        @"add\s+more|increase\s+your|reduce\s+your|cut\s+your|" +
        @"you\s+(may|might|could|can)\s+want|(it|this)\s+(may|might|would|could)\s+be\s+(wise|better|prudent|sensible)|" +
        @"good\s+time|right\s+time|opportunit(y|ies)" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex English();

    // Hinglish in Latin script: kharido / kharid lo (buy), becho / bech do (sell), nikal lo / nikalo (exit),
    // rakho / rakhein (hold), chahiye (should), book karo, jama karo (accumulate), mauka (opportunity).
    [GeneratedRegex(
        @"\b(kharid\w*|khareed\w*|bech\w*|nikal\w*|rakh(o|na|ein|en|iye|e)|chahiye|chaahiye|book\s+kar\w*|jama\s+kar\w*|le\s+lo|mauka|munafa\s+book\w*)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Hinglish();

    /// <summary>The same in Devanagari: buy, sell, exit, should, hold (keep), book, opportunity.</summary>
    private static readonly string[] Devanagari = ["खरीद", "ख़रीद", "बेच", "निकल", "चाहिए", "रखें", "रखो", "रखिए", "बुक कर", "मौका", "मौक़ा"];
}
