using System.Globalization;
using System.Net;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>One pattern on one closed candle: everything a message or a row needs.</summary>
public sealed record PatternOccurrence(
    string Symbol,
    int TimeframeMinutes,
    DateTime BarStartUtc,
    DateTime BarEndUtc,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    int MinutesWithData,
    int MinutesExpected,
    CandlePattern Pattern,
    PatternDirection Direction)
{
    public string DedupeKey =>
        $"patterns:{Symbol}:{TimeframeMinutes}m:{BarStartUtc.ToString("yyyyMMdd'T'HHmm'Z'", CultureInfo.InvariantCulture)}:{CandlePatternCatalog.Key(Pattern)}";
}

/// <summary>The words: alert titles, messages and the grouped Telegram text.</summary>
public static class PatternAlertText
{
    /// <summary>Telegram refuses messages over 4096 characters; long before that a list stops being read.</summary>
    public const int MaxTelegramLines = 25;

    /// <summary>"NSE:NIFTYBANK-INDEX" → "BANKNIFTY", "NSE:SBIN-EQ" → "SBIN", "MCX:CRUDEOIL26SEPFUT" → "CRUDEOIL26SEPFUT".</summary>
    public static string DisplayName(string symbol)
    {
        var spot = UnderlyingCatalog.UnderlyingForSpot(symbol);
        if (spot is not null) return spot;
        int colon = symbol.IndexOf(':');
        return colon >= 0 ? symbol[(colon + 1)..] : symbol;
    }

    /// <summary>
    /// 57210 → "57,210"; 812.3 → "812.30"; 1234567 → "12,34,567". Indian digit
    /// grouping, two decimals only when there are any. By hand rather than
    /// through the en-IN culture, so a host without ICU formats it the same.
    /// </summary>
    public static string Price(decimal value)
    {
        var rounded = Math.Round(Math.Abs(value), 2, MidpointRounding.AwayFromZero);
        var text = rounded == Math.Truncate(rounded)
            ? rounded.ToString("0", CultureInfo.InvariantCulture)
            : rounded.ToString("0.00", CultureInfo.InvariantCulture);

        int dot = text.IndexOf('.');
        var whole = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? string.Empty : text[dot..];

        if (whole.Length > 3)
        {
            var head = whole[..^3];
            var groups = new List<string>();
            while (head.Length > 2)
            {
                groups.Insert(0, head[^2..]);
                head = head[..^2];
            }

            if (head.Length > 0) groups.Insert(0, head);
            whole = string.Join(',', groups) + "," + whole[^3..];
        }

        return (value < 0 && rounded != 0 ? "-" : string.Empty) + whole + fraction;
    }

    public static string IstClock(DateTime utc) => IstTime.ToIst(utc).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"BANKNIFTY 15m doji — neutral".</summary>
    public static string Title(PatternOccurrence o) =>
        $"{DisplayName(o.Symbol)} {o.TimeframeMinutes}m {CandlePatternCatalog.Info(o.Pattern).Name} — {CandlePatternCatalog.DirectionKey(o.Direction)}";

    /// <summary>
    /// "BANKNIFTY 15m doji at 10:30 IST — open 57,210, close 57,214, range 96".
    /// The time is the candle's start, the way a chart labels it.
    /// </summary>
    public static string Message(PatternOccurrence o)
    {
        var text = $"{DisplayName(o.Symbol)} {o.TimeframeMinutes}m {CandlePatternCatalog.Info(o.Pattern).Name} at {IstClock(o.BarStartUtc)} IST — " +
                   $"open {Price(o.Open)}, close {Price(o.Close)}, range {Price(o.High - o.Low)}";
        return o.MinutesWithData < o.MinutesExpected
            ? $"{text} ({o.MinutesWithData} of {o.MinutesExpected} minutes had data)"
            : text;
    }

    /// <summary>
    /// One Telegram message for every pattern on candles that closed at the same
    /// minute, in parse_mode HTML. Beyond <see cref="MaxTelegramLines"/> the rest
    /// are counted, not listed; every one of them is on the console either way.
    /// </summary>
    public static string TelegramMessage(DateTime closedAtUtc, IReadOnlyList<PatternOccurrence> occurrences)
    {
        var lines = new List<string>
        {
            $"<b>Candle patterns · candles closed {IstClock(closedAtUtc)} IST</b>",
        };

        foreach (var o in occurrences.Take(MaxTelegramLines))
        {
            lines.Add($"{WebUtility.HtmlEncode(Message(o))} · <i>{CandlePatternCatalog.DirectionKey(o.Direction)}</i>");
        }

        if (occurrences.Count > MaxTelegramLines)
        {
            lines.Add($"+{occurrences.Count - MaxTelegramLines} more on the Pattern alerts page.");
        }

        return string.Join('\n', lines);
    }
}

/// <summary>
/// At most <c>max</c> messages in any <c>window</c>. A noisy minute costs the
/// chat one grouped message; a noisy hour cannot cost it more than the cap.
/// </summary>
public sealed class SlidingWindowLimiter
{
    private readonly int _max;
    private readonly TimeSpan _window;
    private readonly Queue<DateTime> _sent = new();
    private readonly object _gate = new();

    public SlidingWindowLimiter(int max, TimeSpan window)
    {
        if (max < 1) throw new ArgumentOutOfRangeException(nameof(max));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _max = max;
        _window = window;
    }

    public int Max => _max;
    public TimeSpan Window => _window;

    /// <summary>Takes a slot and returns true, or returns false and takes nothing.</summary>
    public bool TryAcquire(DateTime nowUtc)
    {
        lock (_gate)
        {
            while (_sent.Count > 0 && nowUtc - _sent.Peek() >= _window) _sent.Dequeue();
            if (_sent.Count >= _max) return false;
            _sent.Enqueue(nowUtc);
            return true;
        }
    }
}
