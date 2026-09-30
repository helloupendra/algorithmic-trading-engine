using System.Text;
using System.Text.RegularExpressions;

namespace AlgoTrading.Api.Services.AiTelegram;

/// <summary>
/// The Assistant's Markdown as Telegram HTML, which a phone renders:
/// **bold**, `code`, fenced blocks and tables as monospace blocks, the rest
/// as escaped text. Telegram's HTML is strict; the sender falls back to plain
/// text if it is refused.
/// </summary>
public static partial class TelegramText
{
    /// <summary>Telegram's limit is 4,096 characters a message; this leaves room for the tags.</summary>
    public const int MaxMessage = 3800;

    public static string ToHtml(string markdown)
    {
        var html = new StringBuilder();
        var block = new StringBuilder();
        bool fence = false;

        void FlushBlock()
        {
            if (block.Length == 0) return;
            html.Append("<pre>").Append(Escape(block.ToString().TrimEnd('\n'))).Append("</pre>\n");
            block.Clear();
        }

        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                // Either edge of a fence ends the block before it: a table right above code stays its own block.
                FlushBlock();
                fence = !fence;
                continue;
            }

            // A table is kept as it is, in monospace, where its columns line up.
            bool table = !fence && line.TrimStart().StartsWith('|');
            if (fence || table)
            {
                if (table && TableRule().IsMatch(line)) continue;
                block.Append(line).Append('\n');
                continue;
            }

            FlushBlock();
            var heading = Heading().Match(line);
            html.Append(heading.Success ? $"<b>{Inline(heading.Groups[1].Value)}</b>" : Inline(line)).Append('\n');
        }

        FlushBlock();
        return html.ToString().Trim();
    }

    /// <summary>Text cut into messages at line breaks, each under <see cref="MaxMessage"/>.</summary>
    public static IReadOnlyList<string> Split(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (string line in text.Split('\n'))
        {
            if (current.Length > 0 && current.Length + line.Length + 1 > MaxMessage)
            {
                parts.Add(current.ToString().TrimEnd());
                current.Clear();
            }

            current.Append(line.Length <= MaxMessage ? line : line[..MaxMessage]).Append('\n');
        }

        if (current.ToString().Trim().Length > 0) parts.Add(current.ToString().TrimEnd());
        return parts;
    }

    private static string Inline(string text)
    {
        string t = Escape(text);
        t = Code().Replace(t, "<code>$1</code>");
        t = Bold().Replace(t, "<b>$1</b>");
        return t;
    }

    /// <summary>What Telegram's HTML needs escaped, and only that: other characters (₹, ·, —) stay as they are.</summary>
    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    [GeneratedRegex(@"^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableRule();

    [GeneratedRegex(@"^#{1,6}\s+(.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex Code();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex Bold();
}
