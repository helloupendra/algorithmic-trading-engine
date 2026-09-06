// src/AlgoTrading.Api/Services/ChainPollerSupervisor.cs

using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Owns the option-chain poller process (option_chain_poller.py).
/// </summary>
/// <remarks>
/// This is the only way open interest ever enters the platform: the broker's
/// tick feed does not carry it, so a session with this stopped has prices and
/// volume and no OI at all — permanently, because open interest at 10:15 can
/// only be known if something wrote it down at 10:15.
/// <para>
/// It therefore needs to be as easy to start as the ingestor, and as visible
/// when it is not running. It gets the same supervisor for the same reason.
/// </para>
/// </remarks>
public sealed class ChainPollerSupervisor : PythonDaemonSupervisor
{
    public ChainPollerSupervisor(
        PythonEngineLocator engine,
        IServiceScopeFactory scopeFactory,
        ILogger<ChainPollerSupervisor> logger)
        : base(
            new DaemonDescriptor(
                Name: "chain poller",
                ScriptParts: new[] { "market_data", "live", "option_chain_poller.py" },
                ProcessMarker: ProcessProbe.ChainPollerMarker,
                PidSettingKey: SystemSettingKeys.ChainPollerPid),
            engine, scopeFactory, logger)
    {
    }
}
