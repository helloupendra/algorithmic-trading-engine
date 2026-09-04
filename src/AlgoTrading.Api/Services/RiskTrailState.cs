// src/AlgoTrading.Api/Services/RiskTrailState.cs
namespace AlgoTrading.Api.Services;

/// <summary>
/// One trailing track: whether the trail has armed and, once it has, the best
/// (most favourable) value seen since. A track only ever moves its peak up.
/// </summary>
public readonly record struct TrailTrack(bool Armed, decimal Peak)
{
    public static readonly TrailTrack Idle = new(false, 0m);
}

/// <summary>The two tracks a leg carries: premium points and percent of entry, each with its own trigger and trail.</summary>
public readonly record struct LegTrailTracks(TrailTrack Points, TrailTrack Percent);

/// <summary>
/// The live trailing-stop peaks of one run: one track for the run's total P&amp;L,
/// one per group id, and two per open position (points and percent). Held in
/// memory on the run's registry entry only — never persisted — so an API
/// restart (the run is adopted with a fresh state) or a rule change re-arms
/// every trail from the current P&amp;L.
///
/// Thread-safe: the risk guard sweeps runs on its own timer while the strategy
/// controller may replace the run's rules underneath it.
/// </summary>
public sealed class RiskTrailState
{
    private readonly object _lock = new();
    private readonly Dictionary<string, TrailTrack> _groups = new(StringComparer.Ordinal);
    private readonly Dictionary<long, LegTrailTracks> _legs = new();
    private TrailTrack _overall = TrailTrack.Idle;

    /// <summary>
    /// Records the run's total P&amp;L and returns the resulting track. Arms when
    /// <paramref name="pnl"/> first reaches <paramref name="trigger"/> (or, with
    /// no trigger, as soon as it is positive); afterwards keeps the running max.
    /// </summary>
    public TrailTrack ObserveOverall(decimal pnl, decimal? trigger)
    {
        lock (_lock)
        {
            _overall = Advance(_overall, pnl, trigger);
            return _overall;
        }
    }

    /// <summary>The same for one group's P&amp;L, keyed by its group id.</summary>
    public TrailTrack ObserveGroup(string groupId, decimal pnl, decimal? trigger)
    {
        var key = groupId ?? string.Empty;
        lock (_lock)
        {
            var track = Advance(_groups.TryGetValue(key, out var current) ? current : TrailTrack.Idle, pnl, trigger);
            _groups[key] = track;
            return track;
        }
    }

    /// <summary>
    /// The same for one leg, whose two tracks advance independently: points
    /// against <paramref name="triggerPoints"/>, percent against
    /// <paramref name="triggerPercent"/>.
    /// </summary>
    public LegTrailTracks ObserveLeg(
        long positionId,
        decimal pnlPoints,
        decimal pnlPercent,
        decimal? triggerPoints,
        decimal? triggerPercent)
    {
        lock (_lock)
        {
            var current = _legs.TryGetValue(positionId, out var existing) ? existing : default;
            var tracks = new LegTrailTracks(
                Advance(current.Points, pnlPoints, triggerPoints),
                Advance(current.Percent, pnlPercent, triggerPercent));
            _legs[positionId] = tracks;
            return tracks;
        }
    }

    /// <summary>
    /// Forgets the tracks of positions that are no longer open and of groups
    /// with no open leg left, so a long-running run's state stays bounded.
    /// </summary>
    public void Prune(IReadOnlyCollection<long> openPositionIds, IReadOnlyCollection<string> openGroupIds)
    {
        lock (_lock)
        {
            if (_legs.Count > 0)
            {
                foreach (var id in _legs.Keys.Where(id => !openPositionIds.Contains(id)).ToList())
                {
                    _legs.Remove(id);
                }
            }

            if (_groups.Count > 0)
            {
                foreach (var id in _groups.Keys.Where(id => !openGroupIds.Contains(id)).ToList())
                {
                    _groups.Remove(id);
                }
            }
        }
    }

    /// <summary>
    /// Drops every peak, so each trail arms again from the P&amp;L of the next
    /// sweep. Used when the run's trailing rules change.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _overall = TrailTrack.Idle;
            _groups.Clear();
            _legs.Clear();
        }
    }

    /// <summary>
    /// Arms the track the first time the value reaches the trigger (or turns
    /// positive when there is none), then raises the peak to the best value seen.
    /// </summary>
    private static TrailTrack Advance(TrailTrack track, decimal value, decimal? trigger)
    {
        if (!track.Armed)
        {
            bool arms = trigger.HasValue ? value >= trigger.Value : value > 0m;
            return arms ? new TrailTrack(true, value) : TrailTrack.Idle;
        }

        return value > track.Peak ? track with { Peak = value } : track;
    }
}
