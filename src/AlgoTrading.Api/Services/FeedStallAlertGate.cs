// src/AlgoTrading.Api/Services/FeedStallAlertGate.cs

namespace AlgoTrading.Api.Services;

/// <summary>
/// Which of the runners' feed-stall reports go to Telegram: the first stall per
/// underlying in a ten-minute window, once the silence has lasted 180 s, and a
/// recovery only when that stall was sent.
/// </summary>
/// <remarks>
/// Every run watches its own ticks, so one feed blip is reported by every run
/// on that underlying, twice (stalled, recovered). On 25 Sep 2026 eleven blips
/// made 26 stall and 26 recovery alerts each, 572 in all; Telegram refused 350
/// of them with 429. And the runners report at 90 s of silence, before the
/// feed's own watchdog has had its 120 s to reconnect (core/live/feed_runner.py),
/// so most of those messages described a blip that was already healing.
/// <para>
/// Every report is still logged against its run and kept as an alert_events
/// row; this decides only what reaches the channel. The key is the underlying,
/// not the feed: on 24 Sep at 15:18 SENSEX alone went quiet while NIFTY and
/// BANKNIFTY flowed, and that is exactly the message that must not be folded
/// into another underlying's.
/// </para>
/// <para>
/// In memory: an API restart forgets the window, which costs at most one extra
/// message per underlying.
/// </para>
/// </remarks>
public sealed class FeedStallAlertGate
{
    /// <summary>After a stall is sent for an underlying, its next one waits this long.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A stall shorter than this is not sent. Past the feed's own 120 s
    /// reconnect with room for the reconnect to land; the runner reports again
    /// at this age (core/feed_watchdog.py, DEFAULT_CONFIRM_AFTER_SECONDS).
    /// </summary>
    public const int SendAfterSilentSeconds = 180;

    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _byUnderlying = new(StringComparer.OrdinalIgnoreCase);

    public FeedStallAlertGate(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    private sealed class Entry
    {
        public DateTimeOffset LastStallSent;
        public bool StallOpen;
    }

    /// <summary>
    /// True when this stall report should go to Telegram, and remembers that it
    /// did: the first one per underlying in <see cref="Window"/>, and only once
    /// the silence reaches <see cref="SendAfterSilentSeconds"/>.
    /// </summary>
    public bool ShouldSendStall(string? underlying, int silentSeconds)
    {
        if (silentSeconds < SendAfterSilentSeconds) return false;

        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var key = Key(underlying);
            if (_byUnderlying.TryGetValue(key, out var entry) && now - entry.LastStallSent < Window)
            {
                return false;
            }

            _byUnderlying[key] = new Entry { LastStallSent = now, StallOpen = true };
            return true;
        }
    }

    /// <summary>
    /// True when this recovery should go to Telegram: a stall for the
    /// underlying was sent and no recovery has closed it yet. The first run to
    /// recover speaks for the rest.
    /// </summary>
    public bool ShouldSendRecovery(string? underlying)
    {
        lock (_lock)
        {
            if (!_byUnderlying.TryGetValue(Key(underlying), out var entry) || !entry.StallOpen) return false;
            entry.StallOpen = false;
            return true;
        }
    }

    private static string Key(string? underlying)
        => string.IsNullOrWhiteSpace(underlying) ? "?" : underlying.Trim().ToUpperInvariant();
}
