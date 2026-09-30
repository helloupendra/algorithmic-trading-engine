using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>A slot for one call in flight; disposing it frees the slot.</summary>
public sealed class AiCallLease : IDisposable
{
    private Action? _release;

    internal AiCallLease(Action release) => _release = release;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>Why a call may not start now, and when it may.</summary>
public sealed record AiRefusal(string Error, int RetryAfterSeconds);

/// <summary>
/// The desk's own brake on the hosted models: per user, per minute for the
/// whole desk, and calls in flight at once.
/// </summary>
/// <remarks>
/// <para>
/// In memory, so a restart forgets the windows: the worst case is one extra
/// burst, which the provider's own limit then answers. What it protects is
/// the free tier: a loop in a script or a stuck button would otherwise spend
/// the desk's share of the provider's limit and get every model of the chain
/// a 429 for everyone.
/// </para>
/// <para>
/// A slot is taken with <see cref="TryAcquire"/> and freed by disposing the
/// lease, so a call that throws still frees its slot.
/// </para>
/// </remarks>
public sealed class AiRateLimiter
{
    private static readonly TimeSpan UserWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GlobalWindow = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Queue<DateTime> _global = new();
    private readonly Dictionary<string, Queue<DateTime>> _perUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _inFlightPerUser = new(StringComparer.OrdinalIgnoreCase);
    private readonly IOptionsMonitor<AiSettings> _settings;
    private readonly TimeProvider _time;
    private int _inFlight;

    public AiRateLimiter(IOptionsMonitor<AiSettings> settings, TimeProvider? time = null)
    {
        _settings = settings;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Calls started across the desk in the last minute.</summary>
    public int UsedLastMinute
    {
        get
        {
            lock (_gate)
            {
                Trim(_global, _time.GetUtcNow().UtcDateTime - GlobalWindow);
                return _global.Count;
            }
        }
    }

    public int InFlight
    {
        get { lock (_gate) return _inFlight; }
    }

    /// <summary>A lease for one call, or the refusal in words.</summary>
    public (AiCallLease? Lease, AiRefusal? Refusal) TryAcquire(string user)
    {
        var s = _settings.CurrentValue;
        var now = _time.GetUtcNow().UtcDateTime;

        lock (_gate)
        {
            Trim(_global, now - GlobalWindow);
            if (!_perUser.TryGetValue(user, out var mine))
            {
                mine = new Queue<DateTime>();
                _perUser[user] = mine;
            }

            Trim(mine, now - UserWindow);
            _inFlightPerUser.TryGetValue(user, out int myInFlight);

            if (_inFlight >= s.MaxConcurrent)
            {
                return (null, new AiRefusal($"{_inFlight} AI calls are already running; try again when one finishes.", 10));
            }

            if (myInFlight >= s.MaxConcurrentPerUser)
            {
                return (null, new AiRefusal($"You already have {myInFlight} questions running; wait for one to finish.", 10));
            }

            if (_global.Count >= s.GlobalPerMinute)
            {
                return (null, new AiRefusal(
                    $"The desk has started {_global.Count} AI calls in the last minute (the cap is {s.GlobalPerMinute}).",
                    SecondsUntil(_global.Peek() + GlobalWindow, now)));
            }

            if (mine.Count >= s.PerUserPer10Min)
            {
                return (null, new AiRefusal(
                    $"You have asked {mine.Count} questions in the last ten minutes (the cap is {s.PerUserPer10Min}).",
                    SecondsUntil(mine.Peek() + UserWindow, now)));
            }

            _global.Enqueue(now);
            mine.Enqueue(now);
            _inFlight++;
            _inFlightPerUser[user] = myInFlight + 1;
        }

        return (new AiCallLease(() => Release(user)), null);
    }

    private void Release(string user)
    {
        lock (_gate)
        {
            _inFlight = Math.Max(0, _inFlight - 1);
            if (_inFlightPerUser.TryGetValue(user, out int n))
            {
                if (n <= 1) _inFlightPerUser.Remove(user);
                else _inFlightPerUser[user] = n - 1;
            }
        }
    }

    private static void Trim(Queue<DateTime> window, DateTime since)
    {
        while (window.Count > 0 && window.Peek() <= since) window.Dequeue();
    }

    private static int SecondsUntil(DateTime when, DateTime now) => Math.Max(1, (int)Math.Ceiling((when - now).TotalSeconds));
}
