using System.Net;
using System.Text.RegularExpressions;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// The rules of Telegram's parse_mode HTML, in one place: what must be escaped,
/// which messages already carry their own markup, and what a message reads as
/// once its markup is taken away.
/// </summary>
public static partial class TelegramHtml
{
    /// <summary>
    /// The longest message Telegram takes, in characters after its entities are
    /// parsed. Past it the answer is 400 "message is too long".
    /// </summary>
    public const int MaxMessageChars = 4096;

    /// <summary>
    /// The first thing on every message scripts/telegram_notifier.py formats as
    /// HTML itself (<c>OWN_HEADINGS</c> there; a test keeps the two lists equal).
    /// Its messages come back through the alert subscriber, and escaping them
    /// would put literal &lt;b&gt; tags on the channel.
    /// </summary>
    public static readonly IReadOnlyList<string> NotifierHeadings =
        ["▶️", "⏹️", "🔁", "🟢", "🔴", "✅", "🔻", "⚪", "📡", "🛑", "🔔", "🌙"];

    /// <summary>Only these three matter to Telegram: a bare &lt; or &amp; makes it refuse the whole message.</summary>
    public static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    /// <summary>True for a message the notifier formatted itself, which is sent as it stands.</summary>
    public static bool IsNotifierMessage(string? message)
    {
        var text = message?.TrimStart();
        return !string.IsNullOrEmpty(text) && NotifierHeadings.Any(h => text.StartsWith(h, StringComparison.Ordinal));
    }

    /// <summary>
    /// The message as plain text, for the retry after Telegram refused its HTML:
    /// the tags Telegram knows taken off, entities decoded, and cut to what
    /// Telegram takes. Anything else that looks like a tag (&lt;class 'int'&gt;
    /// in a traceback) is left in, since it is part of what was being said.
    /// </summary>
    public static string ToPlain(string html)
    {
        var text = WebUtility.HtmlDecode(KnownTag().Replace(html, string.Empty));
        return text.Length <= MaxMessageChars ? text : string.Concat(text.AsSpan(0, MaxMessageChars - 1), "…");
    }

    [GeneratedRegex(@"</?(?:b|strong|i|em|u|ins|s|strike|del|a|code|pre|span|tg-spoiler|tg-emoji|blockquote)(?:\s[^<>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnownTag();
}
