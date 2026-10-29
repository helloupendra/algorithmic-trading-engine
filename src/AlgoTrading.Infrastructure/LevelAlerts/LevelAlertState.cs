namespace AlgoTrading.Infrastructure.LevelAlerts;

/// <summary>What the level-alert service last did, for the page. Every member is a snapshot.</summary>
public sealed record LevelAlertSnapshot(
    bool Enabled,
    DateTime StartedUtc,
    DateTime? LastScanUtc,
    double? LastScanMilliseconds,
    int ActiveRules,
    IReadOnlyList<LevelReading> Readings,
    IReadOnlyList<string> Skipped,
    DateTime? LastErrorUtc,
    string? LastError,
    DateTime? LastTelegramUtc,
    int TelegramMessagesSent,
    int TelegramMessagesSuppressed,
    int TelegramMessagesFailed,
    string? LastTelegramProblem);

/// <summary>
/// The service's memory: the last fresh reading of every watched target, which
/// the next one is compared with, and the snapshot the page reads.
/// </summary>
/// <remarks>
/// A singleton shared by the scanner (writer, one scan at a time) and the
/// controller (reader). The readings live here and not in the database: they
/// change every few seconds, and a cross is decided between two of them in
/// one process. After a restart the first reading is a baseline (see
/// <see cref="LevelCross"/>); what must survive a restart, whether a rule is
/// armed, is stored on the rule.
/// </remarks>
public sealed class LevelAlertState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LevelSample> _previous = new(StringComparer.Ordinal);
    private DateTime? _lastPcrReadUtc;
    private LevelAlertSnapshot _snapshot = new(true, DateTime.UtcNow, null, null, 0, [], [], null, null, null, 0, 0, 0, null);

    public LevelAlertSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <summary>The last fresh reading of a target, or null when there is none to compare with.</summary>
    public LevelSample? Previous(string key)
    {
        lock (_gate) return _previous.TryGetValue(key, out var sample) ? sample : null;
    }

    public void Remember(string key, LevelSample sample)
    {
        lock (_gate) _previous[key] = sample;
    }

    /// <summary>
    /// Forgets the targets no rule watches any more, so a rule paused for an
    /// hour starts again from a fresh baseline rather than from a reading an
    /// hour old, and the map never outgrows the rules.
    /// </summary>
    public void KeepOnly(IReadOnlySet<string> keys)
    {
        lock (_gate)
        {
            foreach (var key in _previous.Keys.Where(k => !keys.Contains(k)).ToList()) _previous.Remove(key);
        }
    }

    /// <summary>Whether the chains are due a look: they are captured once a minute, so every few seconds would be waste.</summary>
    public bool PcrDue(DateTime nowUtc, TimeSpan every)
    {
        lock (_gate) return _lastPcrReadUtc is not { } last || nowUtc - last >= every || nowUtc < last;
    }

    public void PcrRead(DateTime nowUtc)
    {
        lock (_gate) _lastPcrReadUtc = nowUtc;
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate) _snapshot = _snapshot with { Enabled = enabled };
    }

    /// <summary>What a scan read, written by the scanner itself: the next scan reads the chains' last readings from here.</summary>
    public void Observed(DateTime scannedUtc, int activeRules, IReadOnlyList<LevelReading> readings, IReadOnlyList<string> skipped)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastScanUtc = scannedUtc,
                ActiveRules = activeRules,
                Readings = readings,
                Skipped = skipped,
            };
        }
    }

    /// <summary>How long the last scan took, as the service timed it.</summary>
    public void ScanTook(double milliseconds)
    {
        lock (_gate) _snapshot = _snapshot with { LastScanMilliseconds = milliseconds };
    }

    public void ScanFailed(DateTime nowUtc, string error)
    {
        lock (_gate) _snapshot = _snapshot with { LastErrorUtc = nowUtc, LastError = error };
    }

    public void TelegramSent(DateTime nowUtc)
    {
        lock (_gate) _snapshot = _snapshot with { LastTelegramUtc = nowUtc, TelegramMessagesSent = _snapshot.TelegramMessagesSent + 1 };
    }

    public void TelegramSuppressed(string reason)
    {
        lock (_gate) _snapshot = _snapshot with { TelegramMessagesSuppressed = _snapshot.TelegramMessagesSuppressed + 1, LastTelegramProblem = reason };
    }

    public void TelegramFailed(string reason)
    {
        lock (_gate) _snapshot = _snapshot with { TelegramMessagesFailed = _snapshot.TelegramMessagesFailed + 1, LastTelegramProblem = reason };
    }
}
