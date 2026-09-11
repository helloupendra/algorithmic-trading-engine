using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Owns the TrueData live feed process (truedata_streamer.py).
/// </summary>
/// <remarks>
/// A second feed rather than a replacement. Both write the same tables under
/// different SourceKeys, so the two can be compared on the same session before
/// anything is routed away from FYERS — and on the trial they must coexist
/// anyway: TrueData allows 50 symbols on one connection and the recording list
/// carries 80.
///
/// <para>Its own marker and its own pid key. Sharing either with the FYERS
/// ingestor would mean a stop aimed at one could kill the other, which is the
/// sort of thing that reads as "the feed died on its own".</para>
/// </remarks>
public sealed class TrueDataFeedSupervisor : PythonDaemonSupervisor
{
    public TrueDataFeedSupervisor(
        PythonEngineLocator engine,
        IServiceScopeFactory scopeFactory,
        ILogger<TrueDataFeedSupervisor> logger)
        : base(
            new DaemonDescriptor(
                Name: "TrueData feed",
                ScriptParts: new[] { "market_data", "live", "truedata_streamer.py" },
                ProcessMarker: ProcessProbe.TrueDataFeedMarker,
                PidSettingKey: SystemSettingKeys.TrueDataFeedPid),
            engine, scopeFactory, logger)
    {
    }
}
