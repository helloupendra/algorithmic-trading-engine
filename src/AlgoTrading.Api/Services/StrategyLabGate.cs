// src/AlgoTrading.Api/Services/StrategyLabGate.cs

namespace AlgoTrading.Api.Services;

/// <summary>
/// How many filter evaluations may run at once: each one is a Python process on
/// the box that runs the live desk.
/// </summary>
/// <remarks>
/// A trader pressing Evaluate waits a couple of seconds; a script firing requests
/// would otherwise start one process per request, for up to a minute each, on two
/// vCPUs during the session. A request past the cap is refused, not queued: a
/// queue only moves the pile-up into held requests.
/// </remarks>
public sealed class StrategyLabGate
{
    /// <summary>Evaluations on the box at once.</summary>
    public const int MaxProcesses = 2;

    /// <summary>Evaluations one account may have at once; the console's button sends one at a time.</summary>
    public const int MaxPerUser = 1;

    private readonly object _lock = new();
    private readonly Dictionary<long, int> _byUser = new();
    private int _total;

    /// <summary>A slot for this account, to dispose when the process is gone; null when there is none free.</summary>
    public IDisposable? TryEnter(long userId)
    {
        lock (_lock)
        {
            int own = _byUser.GetValueOrDefault(userId);
            if (_total >= MaxProcesses || own >= MaxPerUser) return null;

            _byUser[userId] = own + 1;
            _total++;
            return new Slot(this, userId);
        }
    }

    private void Leave(long userId)
    {
        lock (_lock)
        {
            _total--;
            int own = _byUser[userId] - 1;
            if (own == 0) _byUser.Remove(userId);
            else _byUser[userId] = own;
        }
    }

    private sealed class Slot(StrategyLabGate gate, long userId) : IDisposable
    {
        private int _left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0) gate.Leave(userId);
        }
    }
}
