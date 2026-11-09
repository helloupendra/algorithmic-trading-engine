// src/AlgoTrading.Api/Services/ChainPollerSupervisor.cs

using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Owns the option-chain poller process (option_chain_poller.py).
/// </summary>
/// <remarks>
/// The FYERS fallback. Dhan records in its hosted service, so this process may
/// be intentionally stopped while chain ingestion remains healthy.
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
                PidSettingKey: SystemSettingKeys.ChainPollerPid,
                // The name option_chain_poller.py gives its own log.
                LogName: "chain-poller"),
            engine, scopeFactory, logger)
    {
    }
}
