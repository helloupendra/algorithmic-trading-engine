using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>A passage of a doc, before it is embedded.</summary>
public sealed record AiDocPassage(string Path, string Heading, int Ordinal, string Text, string Hash);

/// <summary>
/// Cuts a Markdown doc into passages a search can return whole: one per
/// section, and a long section at its paragraphs, each under
/// <see cref="MaxChars"/> and carrying its heading trail.
/// </summary>
public static partial class AiDocChunker
{
    public const int MaxChars = 1500;

    public static IReadOnlyList<AiDocPassage> Chunk(string path, string markdown)
    {
        // HTML comments hold verification SQL and reviewer notes: not for readers, not for search.
        string text = HtmlComment().Replace(markdown.Replace("\r\n", "\n"), string.Empty);

        var passages = new List<AiDocPassage>();
        var trail = new string[4];
        var section = new StringBuilder();
        bool inFence = false;

        void Flush()
        {
            string body = section.ToString().Trim();
            section.Clear();
            if (body.Length == 0) return;
            string heading = string.Join(" › ", trail.Where(t => !string.IsNullOrEmpty(t)));
            foreach (string piece in Split(body))
            {
                passages.Add(new AiDocPassage(path, heading, passages.Count, piece, Hash(heading, piece)));
            }
        }

        foreach (string line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;
            var m = inFence ? Match.Empty : Heading().Match(line);
            if (m.Success)
            {
                Flush();
                int level = m.Groups[1].Value.Length;
                trail[level - 1] = m.Groups[2].Value.Trim();
                for (int i = level; i < trail.Length; i++) trail[i] = string.Empty;
                continue;
            }

            section.Append(line).Append('\n');
        }

        Flush();
        return passages;
    }

    /// <summary>A section at its blank lines, joined back up to <see cref="MaxChars"/>; a paragraph longer than that at its sentences.</summary>
    private static IEnumerable<string> Split(string body)
    {
        if (body.Length <= MaxChars)
        {
            yield return body;
            yield break;
        }

        var current = new StringBuilder();
        foreach (string paragraph in Regex.Split(body, @"\n\s*\n"))
        {
            foreach (string part in paragraph.Length <= MaxChars ? [paragraph] : Sentences(paragraph))
            {
                if (current.Length > 0 && current.Length + part.Length + 2 > MaxChars)
                {
                    yield return current.ToString().Trim();
                    current.Clear();
                }

                current.Append(part).Append("\n\n");
            }
        }

        if (current.ToString().Trim().Length > 0) yield return current.ToString().Trim();
    }

    private static IEnumerable<string> Sentences(string paragraph)
    {
        var piece = new StringBuilder();
        foreach (string sentence in Regex.Split(paragraph, @"(?<=[.!?])\s+"))
        {
            if (piece.Length > 0 && piece.Length + sentence.Length + 1 > MaxChars)
            {
                yield return piece.ToString();
                piece.Clear();
            }

            piece.Append(sentence.Length > MaxChars ? sentence[..MaxChars] : sentence).Append(' ');
        }

        if (piece.Length > 0) yield return piece.ToString().Trim();
    }

    private static string Hash(string heading, string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(heading + "\n" + text))).ToLowerInvariant();

    [GeneratedRegex(@"<!--[\s\S]*?-->")]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"^(#{1,4})\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();
}
