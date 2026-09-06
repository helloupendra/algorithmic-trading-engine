// src/AlgoTrading.Api/Services/IngestorSupervisor.cs

using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Owns the live data ingestor process (fyers_streamer.py).
/// </summary>
/// <remarks>
/// All of the behaviour is in <see cref="PythonDaemonSupervisor"/>; this names
/// the one process. The launching, pid bookkeeping and adoption-after-restart
/// were written once because the platform runs more than one of these and every
/// one of them has to get the same fiddly details right.
/// </remarks>
public sealed class IngestorSupervisor : PythonDaemonSupervisor
{
    public IngestorSupervisor(
        PythonEngineLocator engine,
        IServiceScopeFactory scopeFactory,
        ILogger<IngestorSupervisor> logger)
        : base(
            new DaemonDescriptor(
                Name: "ingestor",
                ScriptParts: new[] { "market_data", "live", "fyers_streamer.py" },
                ProcessMarker: ProcessProbe.IngestorMarker,
                PidSettingKey: SystemSettingKeys.IngestorPid),
            engine, scopeFactory, logger)
    {
    }
}
