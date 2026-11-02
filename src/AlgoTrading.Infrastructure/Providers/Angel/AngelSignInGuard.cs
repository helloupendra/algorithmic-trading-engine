using System.Globalization;
using AlgoTrading.Infrastructure.Services;

namespace AlgoTrading.Infrastructure.Providers.Angel;

/// <summary>
/// Whether an automatic Angel One sign-in may go now.
/// </summary>
/// <remarks>
/// Every screen call signs in when no session is held, and a failed sign-in used
/// to leave nothing behind: one movers snapshot asked six times, a failed one was
/// cached 45 s, and the page polls every 45 s. With a stale PIN or TOTP that was
/// about 400 wrong sign-ins an hour for as long as the page stayed open, which is
/// how a broker login gets locked.
/// <para>
/// A sign-in Angel refuses holds the next one for <see cref="RefusedHolds"/>, and
/// the <see cref="MaxRefused"/>th stops them: a wrong PIN stays wrong until someone
/// changes it. A failure that says nothing about the credentials (Angel cannot be
/// reached, or is limiting the pace) backs off from <see cref="FirstFailureHold"/>
/// to <see cref="LongestFailureHold"/> and never stops them. A session Angel rejects
/// within <see cref="YoungSession"/> of signing in holds for <see cref="YoungSessionHold"/>,
/// so a bad token cannot turn into a sign-in per call.
/// </para>
/// <para>
/// A person can always try: the connector page's "Sign in again" forces one, and its
/// success clears everything. Its refusal counts like any other. The state is in
/// memory, like the session: the API's restart (every trading morning, and after
/// new credentials are set) starts it again.
/// </para>
/// </remarks>
public sealed class AngelSignInGuard
{
    /// <summary>Refused sign-ins in a row after which only a person starts another.</summary>
    public const int MaxRefused = 3;

    /// <summary>The wait after the first refusal, and after the second.</summary>
    public static IReadOnlyList<TimeSpan> RefusedHolds { get; } = [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];

    public static TimeSpan FirstFailureHold { get; } = TimeSpan.FromMinutes(1);

    public static TimeSpan LongestFailureHold { get; } = TimeSpan.FromMinutes(30);

    /// <summary>A session rejected this soon after it started says the sign-in itself is not working.</summary>
    public static TimeSpan YoungSession { get; } = TimeSpan.FromMinutes(5);

    public static TimeSpan YoungSessionHold { get; } = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private int _refused;
    private int _failed;
    private DateTimeOffset _heldUntil = DateTimeOffset.MinValue;
    private string? _why;

    /// <summary>Refused sign-ins in a row so far.</summary>
    public int Refused
    {
        get
        {
            lock (_lock) return _refused;
        }
    }

    /// <summary>Why no automatic sign-in may go now, in words for the page; null when one may.</summary>
    public string? HeldBecause(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_refused >= MaxRefused)
            {
                return $"Not signing in to Angel One again: it refused {_refused} sign-ins in a row, and more could lock the account. "
                       + $"Its last answer: {_why}. Put the credentials right, then use Sign in again on the connector page.";
            }

            if (now >= _heldUntil) return null;

            string at = IstTime.ToIst(_heldUntil.UtcDateTime).ToString("HH:mm", CultureInfo.InvariantCulture);
            return $"Not signing in to Angel One again before {at} IST. Its last answer: {_why}. Sign in again on the connector page tries now.";
        }
    }

    /// <summary>Angel refused the credentials. What the wait is, or null when only a person starts the next one.</summary>
    public TimeSpan? NoteRefused(string why, DateTimeOffset now)
    {
        lock (_lock)
        {
            _refused++;
            _why = why;
            if (_refused >= MaxRefused) return null;
            var hold = RefusedHolds[Math.Min(_refused, RefusedHolds.Count) - 1];
            _heldUntil = now + hold;
            return hold;
        }
    }

    /// <summary>The sign-in failed in a way that says nothing about the credentials. What the wait is.</summary>
    public TimeSpan NoteFailed(string why, DateTimeOffset now)
    {
        lock (_lock)
        {
            _failed++;
            _why = why;
            var doubled = FirstFailureHold.TotalMinutes * Math.Pow(2, Math.Min(_failed - 1, 16));
            var hold = TimeSpan.FromMinutes(Math.Min(doubled, LongestFailureHold.TotalMinutes));
            _heldUntil = now + hold;
            return hold;
        }
    }

    /// <summary>A session that had only just started was rejected.</summary>
    public void NoteYoungSessionRejected(string why, DateTimeOffset now)
    {
        lock (_lock)
        {
            _why = why;
            _heldUntil = now + YoungSessionHold;
        }
    }

    /// <summary>Signed in: everything before it is over.</summary>
    public void NoteSignedIn()
    {
        lock (_lock)
        {
            _refused = 0;
            _failed = 0;
            _why = null;
            _heldUntil = DateTimeOffset.MinValue;
        }
    }
}
