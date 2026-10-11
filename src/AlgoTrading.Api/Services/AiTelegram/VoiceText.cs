using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AlgoTrading.Api.Services.AiTelegram;

/// <summary>
/// The words around a Telegram voice note: the instruction the audio model is given, what a transcript is asked as,
/// the line a voice question's prompt gets, and the spoken version of an answer.
/// </summary>
public static partial class VoiceText
{
    /// <summary>The instruction that wrote Hinglish voice notes down word for word on 11 Oct 2026.</summary>
    public const string BaseInstruction =
        "Transcribe this voice message word for word. The speaker is Indian and may mix Hindi and English (Hinglish); " +
        "write Hindi words in Roman script, as they would be typed in a chat. Output only the transcript.";

    /// <summary>Words the desk uses that the model would otherwise mishear, whatever the strategies.</summary>
    public static readonly IReadOnlyList<string> DeskWords = ["NIFTY", "BANKNIFTY", "SENSEX", "P&L"];

    /// <summary>How a spoken reply ends when the answer had a table or was cut short.</summary>
    public const string DetailsAbove = "Full details are in the text above.";

    /// <summary>Strategy names a vocabulary hint carries at most.</summary>
    public const int MaxNames = 30;

    /// <summary>
    /// The audio model's instruction: <see cref="BaseInstruction"/>, then the desk's words. Without them "Fulcrum" came
    /// back "Phalacrom" and "Ghost" came back "GOAT" (11 Oct 2026).
    /// </summary>
    public static string Instruction(IReadOnlyList<string> strategyNames, string? name)
    {
        var words = strategyNames.Concat(DeskWords).ToList();
        if (!string.IsNullOrWhiteSpace(name)) words.Add(name.Trim());
        return $"{BaseInstruction} Words you may hear, spelled as the desk spells them: {string.Join(", ", words)}.";
    }

    /// <summary>
    /// Strategy names as they are said: "GhostTangentCrossings" is "Ghost Tangent Crossings", and
    /// "Fulcrum2StraddleBuy20" is "Fulcrum Straddle Buy". Each once, at most <see cref="MaxNames"/>.
    /// </summary>
    public static IReadOnlyList<string> SpokenNames(IEnumerable<string> strategyNames) => strategyNames
        .Select(n => Spaces().Replace(CamelBreak().Replace(Digits().Replace(n, " "), " "), " ").Trim())
        .Where(n => n.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(MaxNames)
        .ToList();

    /// <summary>
    /// The one line a voice question's prompt gets: it was spoken, so a name may be misheard, and the owner may call
    /// the assistant by <paramref name="name"/> (none when it is empty).
    /// </summary>
    public static string PromptNote(string? name) =>
        "The owner asked this in a voice note, written down by machine, so a name in it may be misheard." +
        (string.IsNullOrWhiteSpace(name) ? string.Empty : $" The owner may call you {name.Trim()}; you may answer to that name.");

    /// <summary>
    /// A transcript as it is asked: one that starts with an agent's prefix word, said or written ("Trader, why did you
    /// buy?", "slash trader …", "/trader …"), after an optional address by <paramref name="name"/> ("Jarvis, trader,
    /// …"), becomes that typed prefix ("/trader why did you buy?"). Anything else is asked as heard, with any leading
    /// slash dropped: a voice note never runs /new, /unlink, /remember or another command.
    /// </summary>
    public static string AsTyped(string transcript, IEnumerable<string> agentCommands, string? name)
    {
        string heard = transcript.Trim();
        string rest = heard;
        if (!string.IsNullOrWhiteSpace(name)
            && rest.StartsWith(name.Trim(), StringComparison.OrdinalIgnoreCase)
            && (rest.Length == name.Trim().Length || !char.IsLetterOrDigit(rest[name.Trim().Length])))
        {
            rest = rest[name.Trim().Length..].TrimStart(' ', ',', '.', ':', ';', '!', '?', '-');
        }

        var match = Prefix().Match(rest);
        if (match.Success)
        {
            string command = "/" + match.Groups["word"].Value.ToLowerInvariant();
            if (agentCommands.Contains(command, StringComparer.OrdinalIgnoreCase))
            {
                string question = match.Groups["rest"].Value.Trim();
                return question.Length == 0 ? command : $"{command} {question}";
            }
        }

        return heard.TrimStart('/').Trim();
    }

    /// <summary>
    /// An answer as it is spoken: no tables, code, links, URLs or tool citations, no Markdown marks, numbers as written,
    /// cut at a sentence to <paramref name="maxChars"/>. An answer that had a table or a code block, or was cut, ends
    /// with <see cref="DetailsAbove"/>.
    /// </summary>
    public static string Spoken(string markdown, int maxChars)
    {
        int max = Math.Clamp(maxChars, 80, 3000);
        bool details = false;
        bool fence = false;
        var sentences = new List<string>();
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                fence = !fence;
                details = true;
                continue;
            }

            if (fence) continue;
            if (line.StartsWith('|'))
            {
                details = true;
                continue;
            }

            if (Rule().IsMatch(line)) continue;
            line = Inline(Marker().Replace(line, string.Empty));
            if (line.Length == 0) continue;
            // A heading or a list item ends where its line does.
            sentences.Add(Ends(line) ? line : line + ".");
        }

        string text = Spaces().Replace(string.Join(' ', sentences), " ").Trim();
        string tail = details ? " " + DetailsAbove : string.Empty;
        if (text.Length + tail.Length > max)
        {
            text = Cut(text, max - DetailsAbove.Length - 1);
            tail = " " + DetailsAbove;
        }

        return (text + tail).Trim();
    }

    /// <summary>The text cut after its last whole sentence within <paramref name="room"/>; a first sentence too long is cut at a word.</summary>
    private static string Cut(string text, int room)
    {
        var kept = new StringBuilder();
        foreach (string sentence in SentenceEnd().Split(text))
        {
            if (kept.Length + sentence.Length + 1 > room) break;
            if (kept.Length > 0) kept.Append(' ');
            kept.Append(sentence);
        }

        if (kept.Length > 0) return kept.ToString();
        int at = text.LastIndexOf(' ', Math.Min(room, text.Length - 1));
        return text[..(at > 0 ? at : room)].TrimEnd(',', ';', ':', '-', ' ') + ".";
    }

    private static string Inline(string line)
    {
        string t = Link().Replace(line, "$1");
        t = Url().Replace(t, string.Empty);
        // "(`get_run`, 19:56 IST)": where the answer came from is for the eye, not the ear.
        t = Citation().Replace(t, string.Empty);
        t = ToolName().Replace(t, string.Empty);
        t = Footnote().Replace(t, string.Empty);
        t = t.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
        t = Emphasis().Replace(t, "$1");
        t = t.Replace("→", " to ").Replace("—", ", ").Replace("·", ",");
        // Emoji and other pictographs; currency signs such as ₹ stay.
        var kept = new StringBuilder(t.Length);
        foreach (var rune in t.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.OtherSymbol or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse) continue;
            if (rune.Value is 0x200D or 0xFE0F) continue;
            kept.Append(rune.ToString());
        }

        return Spaces().Replace(kept.ToString(), " ").Replace(" ,", ",").Replace(" .", ".").Trim(' ', ',', ';');
    }

    private static bool Ends(string line) => line[^1] is '.' or '!' or '?' or '।' or ':';

    [GeneratedRegex(@"^(?:/|slash\s+)?(?<word>[A-Za-z]+)(?=$|[\s,.:;!?-])[\s,.:;!?-]*(?<rest>.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Prefix();

    /// <summary>A heading's #, a list's bullet or number, a quote's &gt;.</summary>
    [GeneratedRegex(@"^(?:#{1,6}\s+|[-*+]\s+|\d{1,3}[.)]\s+|>\s*)")]
    private static partial Regex Marker();

    [GeneratedRegex(@"^(?:[-*_]\s*){3,}$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Url();

    /// <summary>A parenthesis that names a tool: <c>(`get_run`, 19:56 IST)</c>, <c>(from get_runs)</c>.</summary>
    [GeneratedRegex(@"\s*\([^()]*\b[a-z]+(?:_[a-z0-9]+)+\b[^()]*\)")]
    private static partial Regex Citation();

    [GeneratedRegex(@"`[a-z]+(?:_[a-z0-9]+)+`")]
    private static partial Regex ToolName();

    [GeneratedRegex(@"\[\d+\]")]
    private static partial Regex Footnote();

    [GeneratedRegex(@"(?<![\w*])[*_](\S(?:[^*_]*\S)?)[*_](?![\w*])")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"(?<=[.!?।])\s+")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex CamelBreak();
}
