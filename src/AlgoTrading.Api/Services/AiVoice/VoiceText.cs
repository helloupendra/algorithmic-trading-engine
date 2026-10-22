using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AlgoTrading.Api.Services.AiVoice;

/// <summary>Whether a hands-free utterance was said to the assistant (<see cref="VoiceText.Wake"/>), and what it asks.</summary>
/// <param name="Question">The transcript without the greeting and the name; empty when not addressed, or when the name was all that was said.</param>
public sealed record VoiceWake(bool Addressed, string Question)
{
    public static readonly VoiceWake NotAddressed = new(false, string.Empty);
}

/// <summary>
/// The words around a question asked by voice, on Telegram or the console: the instruction the audio model is given,
/// what a Telegram transcript is asked as, the line a Telegram voice question's prompt gets, and the spoken version of
/// an answer.
/// </summary>
public static partial class VoiceText
{
    /// <summary>
    /// What the audio model is told. The owner's first real note (11 Oct 2026) came back translated into English, a
    /// question turned into a statement, and a strategy name added from the word list that he never said; hence the
    /// "do not translate, answer or add" and the word list's "only if it is actually said".
    /// </summary>
    public const string BaseInstruction =
        "Transcribe this voice message exactly as spoken, word for word, in the language it is spoken in. Do not " +
        "translate it, answer it, summarise it or correct it, and add no word that was not said; a question stays a " +
        "question. The speaker is Indian and may mix Hindi and English (Hinglish); write Hindi words in Roman script, " +
        "as they would be typed in a chat. Output only the transcript.";

    /// <summary>Words the desk uses that the model would otherwise mishear, whatever the strategies.</summary>
    public static readonly IReadOnlyList<string> DeskWords = ["NIFTY", "BANKNIFTY", "SENSEX", "P&L"];

    /// <summary>How a spoken reply ends when the answer had a table or was cut short.</summary>
    public const string DetailsAbove = "Full details are in the text above.";

    /// <summary>
    /// Whether <paramref name="name"/> may be the assistant's name: 2 to 20 letters, single spaces between words, as
    /// the owner sets it with /name.
    /// </summary>
    public static bool IsName(string? name) =>
        name is not null && NameRx().IsMatch(name.Trim()) && name.Trim().Length is >= 2 and <= 20;

    [GeneratedRegex(@"^\p{L}+(?: \p{L}+)*$")]
    private static partial Regex NameRx();

    /// <summary>Strategy names a vocabulary hint carries at most.</summary>
    public const int MaxNames = 15;

    /// <summary>
    /// The audio model's instruction: <see cref="BaseInstruction"/>, then the desk's words. Without them "Fulcrum" came
    /// back "Phalacrom" and "Ghost" came back "GOAT" (11 Oct 2026).
    /// </summary>
    public static string Instruction(IReadOnlyList<string> strategyNames, string? name)
    {
        var words = strategyNames.Concat(DeskWords).ToList();
        if (!string.IsNullOrWhiteSpace(name)) words.Add(name.Trim());
        return $"{BaseInstruction} Names the speaker may say, spelled as the desk spells them; write one only if it is " +
               $"actually said: {string.Join(", ", words)}.";
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
    /// buy?", "slash trader …", "/trader …"), after an optional address by <paramref name="name"/> ("Ira, trader,
    /// …", "Hey Ira, trader …", "इरा, trader …"), becomes that typed prefix ("/trader why did you buy?"). Anything else
    /// is asked as heard, with any leading slash dropped: a voice note never runs /new, /unlink, /remember or another
    /// command.
    /// </summary>
    /// <remarks>
    /// The address is found as hands-free finds it (<see cref="Wake"/>): by sound, so in Devanagari too, which is how
    /// Amazon Transcribe writes Hindi. The prefix word itself must come out in Latin letters.
    /// </remarks>
    public static string AsTyped(string transcript, IEnumerable<string> agentCommands, string? name)
    {
        string heard = transcript.Trim();
        var address = Wake(heard, name);
        string rest = address.Addressed ? address.Question : heard;

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

    // ---------- hands-free: was it said to the assistant? ----------
    // The sound maps come first: the greeting keys below are made from them as the class is initialised, in this order.

    private static readonly char[] WakeTrim = [' ', '\t', '\n', ',', '.', ':', ';', '!', '?', '-', '–', '—', '।', '…', '"', '\'', '“', '”'];

    private const char Nukta = '\u093C';
    private const char Virama = '\u094D';
    private const char Anusvara = '\u0902';
    private const char Chandrabindu = '\u0901';
    private const char Visarga = '\u0903';

    private static readonly Dictionary<char, string> Vowels = new()
    {
        ['अ'] = "a", ['आ'] = "aa", ['इ'] = "i", ['ई'] = "ii", ['उ'] = "u", ['ऊ'] = "uu", ['ऋ'] = "ri",
        ['ए'] = "e", ['ऐ'] = "ai", ['ओ'] = "o", ['औ'] = "au", ['ऍ'] = "e", ['ऑ'] = "o",
    };

    private static readonly Dictionary<char, string> VowelSigns = new()
    {
        ['\u093E'] = "aa", ['\u093F'] = "i", ['\u0940'] = "ii", ['\u0941'] = "u", ['\u0942'] = "uu", ['\u0943'] = "ri",
        ['\u0947'] = "e", ['\u0948'] = "ai", ['\u094B'] = "o", ['\u094C'] = "au", ['\u0945'] = "e", ['\u0949'] = "o",
    };

    private static readonly Dictionary<char, string> Consonants = new()
    {
        ['क'] = "k", ['ख'] = "kh", ['ग'] = "g", ['घ'] = "gh", ['ङ'] = "n",
        ['च'] = "ch", ['छ'] = "chh", ['ज'] = "j", ['झ'] = "jh", ['ञ'] = "n",
        ['ट'] = "t", ['ठ'] = "th", ['ड'] = "d", ['ढ'] = "dh", ['ण'] = "n",
        ['त'] = "t", ['थ'] = "th", ['द'] = "d", ['ध'] = "dh", ['न'] = "n",
        ['प'] = "p", ['फ'] = "ph", ['ब'] = "b", ['भ'] = "bh", ['म'] = "m",
        ['य'] = "y", ['र'] = "r", ['ल'] = "l", ['व'] = "v",
        ['श'] = "sh", ['ष'] = "sh", ['स'] = "s", ['ह'] = "h",
        ['\u0958'] = "q", ['\u0959'] = "kh", ['\u095A'] = "g", ['\u095B'] = "z", ['\u095C'] = "r", ['\u095D'] = "rh", ['\u095E'] = "f", ['\u095F'] = "y",
    };

    /// <summary>A consonant with a nukta under it, written as two characters: ज़ is z, फ़ is f, ड़ is r.</summary>
    private static readonly Dictionary<char, string> NuktaSounds = new()
    {
        ['क'] = "q", ['ख'] = "kh", ['ग'] = "g", ['ज'] = "z", ['ड'] = "r", ['ढ'] = "rh", ['फ'] = "f", ['य'] = "y",
    };

    /// <summary>The words a hands-free utterance may open with before the name: "Hey Ira", "Arre suno Ira".</summary>
    public static readonly IReadOnlyList<string> WakeGreetings = ["hey", "hi", "hello", "ok", "okay", "suno", "suniye", "arre", "oye"];

    /// <summary>How far into an utterance the name may start: within its first three words.</summary>
    public const int WakeWindow = 3;

    private static readonly HashSet<string> GreetingKeys = WakeGreetings.Select(WakeKey).ToHashSet(StringComparer.Ordinal);

    /// <summary>A word said after the name out of respect ("Ira ji, …"): part of the address, not of the question.</summary>
    private static readonly HashSet<string> HonorificKeys = [WakeKey("ji")];

    /// <summary>
    /// Whether a hands-free utterance was said to the assistant, and the question it asks. Addressed: the assistant's
    /// <paramref name="name"/> (or one of <paramref name="aliases"/>) comes first, within the first
    /// <see cref="WakeWindow"/> words, after any of <see cref="WakeGreetings"/>: "Ira, aaj ka P&amp;L?", "Hey Ira …",
    /// "Ok hey Ira …". The question is the rest of the transcript as it was written, from the word after the name (and
    /// an "ji" after it), with the first letter capitalised; empty when the name was all that was said.
    /// </summary>
    /// <remarks>
    /// Words are compared by <see cref="WakeKey"/>, so case, punctuation, Devanagari spelling (इरा is Ira) and the usual
    /// mishearings of a vowel or a trailing h ("Eera", "Irah", "Aira", "Era") all match, for any name. A name of five
    /// or more sounds also matches with one sound wrong ("Jervis" for Jarvis); a shorter one must match exactly, or
    /// "Ara" would wake "Ira". The name may come split over two words, as a transcript may write it ("I ra").
    /// </remarks>
    public static VoiceWake Wake(string transcript, string? name, IEnumerable<string>? aliases = null)
    {
        var words = WordRx().Matches(transcript ?? string.Empty);
        var names = new[] { name ?? string.Empty }.Concat(aliases ?? [])
            .Select(n => (Key: string.Concat(WordRx().Matches(n).Select(w => WakeKey(w.Value))), Words: WordRx().Count(n)))
            .Where(n => n.Key.Length > 0)
            .ToList();
        if (names.Count == 0) return VoiceWake.NotAddressed;
        int span = names.Max(n => n.Words) + 1;

        for (int i = 0; i < Math.Min(WakeWindow, words.Count); i++)
        {
            for (int length = 1; length <= span && i + length <= words.Count; length++)
            {
                string heard = string.Concat(Enumerable.Range(i, length).Select(k => WakeKey(words[k].Value)));
                if (!names.Any(n => SoundsLike(heard, n.Key))) continue;

                int last = i + length - 1;
                if (last + 1 < words.Count && HonorificKeys.Contains(WakeKey(words[last + 1].Value))) last++;
                string rest = transcript![(words[last].Index + words[last].Length)..].TrimStart(WakeTrim).TrimEnd();
                return new VoiceWake(true, rest.Length > 0 && char.IsLower(rest[0]) ? char.ToUpperInvariant(rest[0]) + rest[1..] : rest);
            }

            if (!GreetingKeys.Contains(WakeKey(words[i].Value))) break;
        }

        return VoiceWake.NotAddressed;
    }

    /// <summary>
    /// A word as it sounds, for <see cref="Wake"/>: lower case, Devanagari written in Roman letters (<see cref="Romanize"/>),
    /// accents and anything not a letter dropped, then the spellings Indian English and a speech model mix up made one:
    /// ph is f; an h after a consonant goes (bh, dh, kh, sh, th); w is v, z is j, q is k, x is ks; y and e are i, o is u;
    /// ai is i and au is u; a doubled letter is one; a final h goes. "Eera", "Irah", "Aira", "Era" and इरा are all "ira".
    /// </summary>
    public static string WakeKey(string word)
    {
        string roman = Romanize(word.ToLowerInvariant());
        try
        {
            roman = roman.Normalize(NormalizationForm.FormD);
        }
        catch (ArgumentException)
        {
            // Not well-formed Unicode (a lone surrogate): compared as it is.
        }

        var letters = new StringBuilder(roman.Length);
        foreach (char c in roman)
        {
            if (c is >= 'a' and <= 'z') letters.Append(c);
        }

        string s = letters.ToString().Replace("ph", "f", StringComparison.Ordinal);
        s = AspirateRx().Replace(s, "$1");
        s = s.Replace('w', 'v').Replace('z', 'j').Replace('q', 'k').Replace("x", "ks", StringComparison.Ordinal)
            .Replace('y', 'i').Replace('e', 'i').Replace('o', 'u');
        s = RepeatRx().Replace(s, "$1");
        s = s.Replace("ai", "i", StringComparison.Ordinal).Replace("au", "u", StringComparison.Ordinal);
        s = RepeatRx().Replace(s, "$1");
        return s.Length > 2 ? s.TrimEnd('h') : s;
    }

    /// <summary>
    /// Devanagari in Roman letters, as Hinglish is typed: इरा is "iraa", कविता is "kavitaa", राम is "raam" (a word's last
    /// consonant has no vowel of its own). Anything else is left as it is.
    /// </summary>
    public static string Romanize(string text)
    {
        var o = new StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (Consonants.TryGetValue(c, out string? sound))
            {
                if (i + 1 < text.Length && text[i + 1] == Nukta)
                {
                    sound = NuktaSounds.GetValueOrDefault(c, sound);
                    i++;
                }

                o.Append(sound);
                char next = i + 1 < text.Length ? text[i + 1] : '\0';
                if (VowelSigns.TryGetValue(next, out string? vowel))
                {
                    o.Append(vowel);
                    i++;
                }
                else if (next == Virama)
                {
                    i++;
                }
                else if (Consonants.ContainsKey(next) || Vowels.ContainsKey(next) || next is Anusvara or Chandrabindu or Visarga)
                {
                    // The inherent vowel, said everywhere but at the end of a word.
                    o.Append('a');
                }
            }
            else if (Vowels.TryGetValue(c, out string? v))
            {
                o.Append(v);
            }
            else if (c is Anusvara or Chandrabindu)
            {
                o.Append('n');
            }
            else if (c == Visarga)
            {
                o.Append('h');
            }
            else if (c == Nukta || c == Virama || VowelSigns.ContainsKey(c))
            {
                // A mark with no consonant before it.
            }
            else
            {
                o.Append(c);
            }
        }

        return o.ToString();
    }

    /// <summary>
    /// Whether a heard word's key is the name's: the same, or for a name of five or more sounds one sound added, dropped
    /// or changed.
    /// </summary>
    private static bool SoundsLike(string heard, string name) =>
        heard == name || (name.Length >= 5 && heard.Length >= 4 && Math.Abs(heard.Length - name.Length) <= 1 && WithinOneEdit(heard, name));

    private static bool WithinOneEdit(string a, string b)
    {
        if (a.Length > b.Length) (a, b) = (b, a);
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j])
            {
                i++;
                j++;
                continue;
            }

            if (++edits > 1) return false;
            if (a.Length == b.Length) i++;
            j++;
        }

        return edits + (b.Length - j) + (a.Length - i) <= 1;
    }

    [GeneratedRegex(@"[\p{L}\p{M}\p{Nd}]+")]
    private static partial Regex WordRx();

    [GeneratedRegex("([^aeiouh])h")]
    private static partial Regex AspirateRx();

    [GeneratedRegex("(.)\\1+")]
    private static partial Regex RepeatRx();

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
