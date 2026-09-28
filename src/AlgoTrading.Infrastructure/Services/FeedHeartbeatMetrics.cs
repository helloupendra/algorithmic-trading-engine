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
/// </remarks>
public static class FeedHeartbeatMetrics
{
    private static readonly string[] Labels = ["source"];

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

    /// <summary>Mirrors what the heartbeat reports. A figure the feed did not send is left as it was.</summary>
    public static void Record(UpsertHeartbeatRequest heartbeat)
    {
        var source = string.IsNullOrWhiteSpace(heartbeat.FeedKey) ? heartbeat.SourceName : heartbeat.FeedKey!;
        if (string.IsNullOrWhiteSpace(source)) return;

        if (heartbeat.QueueDepth is { } depth) QueueDepth.WithLabels(source).Set(depth);
        if (heartbeat.TicksDropped is { } dropped) TicksDropped.WithLabels(source).Set(dropped);
        if (heartbeat.TicksNotStored is { } notStored) TicksNotStored.WithLabels(source).Set(notStored);
        if (heartbeat.TicksRejected is { } rejected) TicksRejected.WithLabels(source).Set(rejected);
        if (heartbeat.GreeksUnavailable is { } reason)
        {
            GreeksAvailable.WithLabels(source).Set(string.IsNullOrWhiteSpace(reason) ? 1 : 0);
        }
    }
}
