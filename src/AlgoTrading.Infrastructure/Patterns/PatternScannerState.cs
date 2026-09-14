namespace AlgoTrading.Infrastructure.Patterns;

/// <summary>What the scanner last did, for the status endpoint. A singleton; every member is a snapshot.</summary>
public sealed record PatternScannerSnapshot(
    bool Enabled,
    DateTime StartedUtc,
    DateTime? LastScanUtc,
    double? LastScanMilliseconds,
    PatternScanOutcome? LastOutcome,
    DateTime? LastErrorUtc,
    string? LastError,
    DateTime? LastTelegramUtc,
    int TelegramMessagesSent,
    int TelegramMessagesSuppressed,
    int TelegramMessagesFailed,
    string? LastTelegramProblem);

/// <summary>
/// Shared between the hosted scanner (writer) and the controller (reader).
/// Counters are since this API process started; the database holds the day.
/// </summary>
public sealed class PatternScannerState
{
    private readonly object _gate = new();
    private PatternScannerSnapshot _snapshot = new(true, DateTime.UtcNow, null, null, null, null, null, null, 0, 0, 0, null);

    public PatternScannerSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate) _snapshot = _snapshot with { Enabled = enabled };
    }

    public void ScanCompleted(PatternScanOutcome outcome, double milliseconds)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                LastScanUtc = outcome.ScannedUtc,
                LastScanMilliseconds = milliseconds,
                LastOutcome = outcome,
            };
        }
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
        lock (_gate)
            _snapshot = _snapshot with { TelegramMessagesSuppressed = _snapshot.TelegramMessagesSuppressed + 1, LastTelegramProblem = reason };
    }

    public void TelegramFailed(string reason)
    {
        lock (_gate)
            _snapshot = _snapshot with { TelegramMessagesFailed = _snapshot.TelegramMessagesFailed + 1, LastTelegramProblem = reason };
    }
}
