using System.Collections.Concurrent;
using AlgoTrading.Contracts.LiveData;
using Prometheus;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// A live feed's own account of what it could not deliver, from its heartbeat,
/// on /metrics per feed.
/// </summary>
/// <remarks>
/// The feed has counted its backlog and the ticks it shed since 2026-09-07, and
/// sent both in every heartbeat — where they stopped: the heartbeat DTO had no
/// such properties, so model binding dropped them and nothing anywhere could
/// see a feed shedding ticks. These are gauges, not counters: the feed does the
/// counting, from its own start, and this mirrors the latest figure it sent.
/// <para>
/// The label is the caller's own feed key, so it is bounded here
/// (<see cref="MaxSources"/>, <see cref="IsWellFormed"/>). A labelled series
/// lives as long as the API process, and on 28 Sep any signed-in trader could
/// post a heartbeat with a new key on every call: five new series each time,
/// in the process that also books fills and runs the risk guard. The endpoint
/// is now for the feeds and admins only; the cap stays so that even they
/// cannot grow /metrics without end.
/// </para>
/// </remarks>
public static class FeedHeartbeatMetrics
{
    /// <summary>
    /// The most distinct feeds the gauges carry. The desk runs three or four
    /// (dhan, fyers, truedata, a dev replay); the rest is headroom.
    /// </summary>
    public const int MaxSources = 16;

    /// <summary>The longest feed name taken as a label.</summary>
    public const int MaxSourceLength = 64;

    private static readonly string[] Labels = ["source"];

    private static readonly LabelBudget Sources = new(MaxSources);

    /// <summary>
    /// Heartbeats whose feed name was malformed or past <see cref="MaxSources"/>,
    /// so their figures were left off /metrics. One unlabelled series: a
    /// refusal is counted, never silent, and cannot itself grow the page.
    /// </summary>
    public static readonly Counter SourcesRefused = Metrics.CreateCounter(
        "algotrading_feed_heartbeat_sources_refused_total",
        "Feed heartbeats left off the feed gauges: a malformed feed name, or more distinct feeds than the cap");

    public static readonly Gauge QueueDepth = Metrics.CreateGauge(
        "algotrading_feed_queue_depth",
        "Ticks buffered in the feed and not yet posted to the API",
        new GaugeConfiguration { LabelNames = Labels });

    public static readonly Gauge TicksDropped = Metrics.CreateGauge(
        "algotrading_feed_ticks_dropped",
        "Ticks the feed's full buffer shed since the feed started",
        new GaugeConfiguration { LabelNames = Labels });

    public static readonly Gauge TicksNotStored = Metrics.CreateGauge(
        "algotrading_feed_ticks_not_stored",
        "Ticks in batches the API refused or never answered, since the feed started",
        new GaugeConfiguration { LabelNames = Labels });

    public static readonly Gauge TicksRejected = Metrics.CreateGauge(
        "algotrading_feed_ticks_rejected",
        "Ticks the feed could not handle at all (neither published nor stored), since the feed started",
        new GaugeConfiguration { LabelNames = Labels });

    public static readonly Gauge GreeksAvailable = Metrics.CreateGauge(
        "algotrading_feed_greeks_available",
        "1 while the feed can compute option IV and greeks, 0 when its pricing library failed to load",
        new GaugeConfiguration { LabelNames = Labels });

    /// <summary>
    /// Mirrors what the heartbeat reports. A figure the feed did not send is
    /// left as it was. False, and nothing recorded, for a feed name that is
    /// malformed or past the cap on distinct feeds.
    /// </summary>
    public static bool Record(UpsertHeartbeatRequest heartbeat) => Record(heartbeat, Sources);

    internal static bool Record(UpsertHeartbeatRequest heartbeat, LabelBudget sources)
    {
        var source = (string.IsNullOrWhiteSpace(heartbeat.FeedKey) ? heartbeat.SourceName : heartbeat.FeedKey!)?.Trim();
        if (string.IsNullOrEmpty(source)) return false;

        if (!IsWellFormed(source) || !sources.TryAdmit(source))
        {
            SourcesRefused.Inc();
            return false;
        }

        if (heartbeat.QueueDepth is { } depth) QueueDepth.WithLabels(source).Set(depth);
        if (heartbeat.TicksDropped is { } dropped) TicksDropped.WithLabels(source).Set(dropped);
        if (heartbeat.TicksNotStored is { } notStored) TicksNotStored.WithLabels(source).Set(notStored);
        if (heartbeat.TicksRejected is { } rejected) TicksRejected.WithLabels(source).Set(rejected);
        if (heartbeat.GreeksUnavailable is { } reason)
        {
            GreeksAvailable.WithLabels(source).Set(string.IsNullOrWhiteSpace(reason) ? 1 : 0);
        }

        return true;
    }

    /// <summary>
    /// A feed name as the feeds write them ("dhan", "python-truedata-recap"):
    /// letters, digits, '.', '_', '-' and ':', at most <see cref="MaxSourceLength"/>.
    /// </summary>
    public static bool IsWellFormed(string source)
    {
        if (source.Length is 0 or > MaxSourceLength) return false;
        foreach (char c in source)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':')) return false;
        }
        return true;
    }
}

/// <summary>Admits label values up to a fixed number of distinct ones; a value once admitted always is.</summary>
public sealed class LabelBudget(int capacity)
{
    private readonly ConcurrentDictionary<string, byte> _admitted = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public int Count => _admitted.Count;

    public bool TryAdmit(string value)
    {
        if (_admitted.ContainsKey(value)) return true;

        lock (_gate)
        {
            if (_admitted.ContainsKey(value)) return true;
            if (_admitted.Count >= capacity) return false;
            _admitted[value] = 0;
            return true;
        }
    }
}
