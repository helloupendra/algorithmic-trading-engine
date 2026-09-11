// src/AlgoTrading.Api/Services/IngestorSupervisor.cs

using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Providers.Fyers;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Owns the FYERS live feed (run_feed.py --vendor fyers), historically "the ingestor".
/// </summary>
/// <remarks>
/// Launched and recognised exactly like every other vendor's feed
/// (<see cref="FeedSupervisor.Describe"/>), and listed with them by
/// <see cref="FeedSupervisorRegistry"/>. It stays a type of its own for two
/// reasons that predate the other feeds. The market-open script, the notifier
/// and the console header all reach it through <c>/api/Ingestor</c>, and they
/// must drive this same instance or two supervisors would each think they own
/// the process. And its pid has always been kept under
/// <see cref="SystemSettingKeys.IngestorPid"/>; a new key would lose track of a
/// feed that was running when the change was deployed.
/// </remarks>
public sealed class IngestorSupervisor : PythonDaemonSupervisor
{
    public IngestorSupervisor(
        PythonEngineLocator engine,
        IServiceScopeFactory scopeFactory,
        ILogger<IngestorSupervisor> logger)
        : base(
            // "ingestor" rather than "FYERS feed": the name is in every log line
            // and status message the desk scripts and operators already know.
            FeedSupervisor.Describe(FyersProvider.Key, name: "ingestor", SystemSettingKeys.IngestorPid),
            engine, scopeFactory, logger)
    {
    }
}
