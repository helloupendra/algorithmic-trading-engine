using System.Collections.Concurrent;

namespace AlgoTrading.Infrastructure.Ai;

/// <summary>A model's health as the desk has seen it lately.</summary>
/// <param name="State">
/// <c>healthy</c> (its last attempt answered); <c>failed</c> (its last attempt
/// failed, not yet enough to cool it); <c>cooling</c> until
/// <paramref name="CoolingUntilUtc"/>; <c>unknown</c> when never asked.
/// </param>
public sealed record AiModelHealthState(
    string Model,
    string State,
    DateTime? CoolingUntilUtc,
    int ConsecutiveFailures,
    string? LastFailure,
    DateTime? LastFailureUtc,
    double? LastOkSeconds,
    DateTime? LastOkUtc,
    DateTime? LastProbeUtc);

/// <summary>
/// What the desk has learnt about each model from its own calls: which ones
/// are answering, and which ones are queueing and should wait at the back of
/// their chains for a while.
/// </summary>
/// <remarks>
/// <para>
/// On the free tier a model can sit in a queue for minutes: on 30 Sep
/// Kimi K3, GLM-5.3 and DeepSeek V4.1 Flash sent not even headers for 100 s
/// while NVIDIA's own models answered in about a second. A chain that still
/// asks a queued model first pays the full first-token wait on every
/// question. So a model whose failure cost a long wait, or that failed twice
/// in a row, <b>cools</b>: for 10 minutes, then 20, 40, at most 60 if it
/// keeps failing, it is asked after the chain's healthy models instead of in
/// its place. It is never dropped: when every healthy model fails, a cooling
/// one is still asked.
/// </para>
/// <para>
/// One success makes a model healthy again. <see cref="AiHealthProbe"/> asks
/// each model whose cooling has ended one tiny question, so a recovered model
/// is back at its place before a person's question has to find out.
/// </para>
/// <para>
/// In memory: a restart forgets it, and the probe re-reads the last half hour
/// of calls to start from what the desk last saw.
/// </para>
/// </remarks>
public sealed class AiModelHealth
{
    public static readonly TimeSpan FirstCooling = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxCooling = TimeSpan.FromMinutes(60);

    private readonly ConcurrentDictionary<string, Entry> _models = new(StringComparer.Ordinal);

    private sealed class Entry
    {
        public int Failures;
        public int Coolings;
        public DateTime? CoolingUntil;
        public string? LastFailure;
        public DateTime? LastFailureUtc;
        public double? LastOkSeconds;
        public DateTime? LastOkUtc;
        public DateTime? LastProbeUtc;
    }

    /// <summary>Learns from one attempt. <paramref name="slow"/>: the failure cost a long wait (a timeout, a queue).</summary>
    public void Record(string model, bool ok, double seconds, bool slow, string outcome, DateTime nowUtc)
    {
        var e = _models.GetOrAdd(model, _ => new Entry());
        lock (e)
        {
            if (ok)
            {
                e.Failures = 0;
                e.Coolings = 0;
                e.CoolingUntil = null;
                e.LastOkSeconds = Math.Round(seconds, 2);
                e.LastOkUtc = nowUtc;
                return;
            }

            e.Failures++;
            e.LastFailure = outcome;
            e.LastFailureUtc = nowUtc;
            if (slow || e.Failures >= 2)
            {
                var span = TimeSpan.FromTicks(Math.Min(MaxCooling.Ticks, FirstCooling.Ticks << Math.Min(e.Coolings, 3)));
                e.Coolings++;
                e.CoolingUntil = nowUtc + span;
            }
        }
    }

    public void Probed(string model, DateTime nowUtc)
    {
        var e = _models.GetOrAdd(model, _ => new Entry());
        lock (e) e.LastProbeUtc = nowUtc;
    }

    public bool IsCooling(string model, DateTime nowUtc) =>
        _models.TryGetValue(model, out var e) && e.CoolingUntil is DateTime until && until > nowUtc;

    /// <summary>The chain as it should be walked now: healthy models in their order, then cooling ones in theirs.</summary>
    public IReadOnlyList<string> Order(IEnumerable<string> chain, DateTime nowUtc)
    {
        var list = chain.ToList();
        return list.Where(m => !IsCooling(m, nowUtc)).Concat(list.Where(m => IsCooling(m, nowUtc))).ToList();
    }

    /// <summary>Models whose cooling has ended but that have not answered since: the probe's work.</summary>
    public IReadOnlyList<string> DueForProbe(DateTime nowUtc) =>
        _models.Where(kv =>
            {
                lock (kv.Value)
                {
                    return kv.Value.CoolingUntil is DateTime until && until <= nowUtc;
                }
            })
            .Select(kv => kv.Key)
            .ToList();

    public AiModelHealthState State(string model, DateTime nowUtc)
    {
        if (!_models.TryGetValue(model, out var e)) return new AiModelHealthState(model, "unknown", null, 0, null, null, null, null, null);
        lock (e)
        {
            bool cooling = e.CoolingUntil is DateTime until && until > nowUtc;
            string state = cooling ? "cooling" : e.Failures > 0 ? "failed" : "healthy";
            return new AiModelHealthState(model, state, cooling ? e.CoolingUntil : null, e.Failures, e.LastFailure, e.LastFailureUtc,
                e.LastOkSeconds, e.LastOkUtc, e.LastProbeUtc);
        }
    }

    public IReadOnlyList<AiModelHealthState> Snapshot(DateTime nowUtc) =>
        _models.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => State(k, nowUtc)).ToList();
}
