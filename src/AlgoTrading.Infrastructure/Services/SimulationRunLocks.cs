// src/AlgoTrading.Infrastructure/Services/SimulationRunLocks.cs
using System.Collections.Concurrent;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// One asynchronous gate per simulation run, process-wide, so every mutation
/// of a run's paper positions (a runner's signal, the risk guard's leg/group
/// close, a stop's flatten) is applied one at a time. The writers run on
/// separate scoped DbContexts with no concurrency token on PaperPosition;
/// without this gate two closes of the same leg both find it open, both file
/// a closing order and the last writer's realized P&amp;L wins — or a runner's
/// CLOSE_GROUP lands after a guard close and opens a reverse position.
/// The gates are never removed: a run id is a handful of bytes and the
/// number of runs per API lifetime is small.
/// </summary>
public static class SimulationRunLocks
{
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> Gates = new();

    /// <summary>Waits for the run's gate; dispose the result to release it. Not re-entrant.</summary>
    public static async Task<IDisposable> AcquireAsync(long simulationRunId, CancellationToken cancellationToken = default)
    {
        var gate = Gates.GetOrAdd(simulationRunId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(gate);
    }

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Releaser(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.Release();
        }
    }
}
