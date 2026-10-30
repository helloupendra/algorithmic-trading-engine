using System.Text;
using System.Text.RegularExpressions;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>
/// Keeps a model's own tool-call markup out of its answer text. DeepSeek V3.2 on
/// Bedrock sometimes writes the opening of its call format into the content
/// ("&lt;｜DSML｜function_calls" on 31 Oct 2026, under a News Analyst answer)
/// before the same call arrives properly as a tool call, so the console showed
/// the markup as words. Everything from the marker on is dropped for the rest of
/// that answer: what follows it is call syntax, never prose.
/// </summary>
/// <remarks>
/// Text arrives in pieces and a marker can be split between two, so a piece's
/// tail that could still become a marker is held back until the next piece (or
/// <see cref="Flush"/>) shows what it is. The bars are DeepSeek's full-width
/// "｜" or a plain "|", with or without spaces.
/// </remarks>
public sealed partial class ToolMarkupFilter
{
    [GeneratedRegex(@"<\s*[|｜]\s*DSML\s*[|｜]", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    // The longest text that can still be the start of a marker: "<", spaces, a bar, spaces, "DSML", spaces.
    private const int MaxPending = 24;

    private readonly StringBuilder _pending = new();
    private bool _cut;

    /// <summary>The part of <paramref name="piece"/> that is safe to show now.</summary>
    public string Feed(string piece)
    {
        if (_cut || string.IsNullOrEmpty(piece)) return string.Empty;
        _pending.Append(piece);
        string text = _pending.ToString();

        var marker = Marker().Match(text);
        if (marker.Success)
        {
            _cut = true;
            _pending.Clear();
            return text[..marker.Index].TrimEnd();
        }

        // Hold back a tail that might be the start of a marker, the "<" onwards.
        int open = text.LastIndexOf('<');
        if (open >= 0 && text.Length - open <= MaxPending && CouldBeMarker(text[open..]))
        {
            _pending.Clear().Append(text[open..]);
            return text[..open];
        }

        _pending.Clear();
        return text;
    }

    /// <summary>What was held back at the end of the answer: it was not a marker after all.</summary>
    public string Flush()
    {
        if (_cut) return string.Empty;
        string rest = _pending.ToString();
        _pending.Clear();
        return rest;
    }

    /// <summary>Whether the whole of <paramref name="tail"/> could still grow into a marker.</summary>
    private static bool CouldBeMarker(string tail)
    {
        // Drop the spaces and bars, then what is left must be a prefix of "DSML" after the "<".
        var compact = new StringBuilder();
        foreach (char c in tail[1..])
        {
            if (char.IsWhiteSpace(c) || c is '|' or '｜') continue;
            compact.Append(c);
        }

        return "DSML".StartsWith(compact.ToString(), StringComparison.Ordinal);
    }
}
