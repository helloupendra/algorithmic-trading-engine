using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Api.Services.Replay;

/// <summary>
/// Owns the market replay's player: <c>market_data/replay/run_replay.py</c>, started per replay
/// with its day, speed, start time and runs.
/// </summary>
/// <remarks>
/// Deliberately not a feed (<see cref="FeedSupervisorRegistry"/>): a feed is stopped at 15:30, watched
/// by failover and by Sentinel's feed rules, and stopped by the close job, and a replay is none of those
/// things. All the process handling (drained pipes, the pid kept across an API restart, never killing a
/// pid that cannot be verified) is inherited.
/// </remarks>
public sealed class ReplaySupervisor(PythonEngineLocator engine, IServiceScopeFactory scopeFactory, ILogger<ReplaySupervisor> logger)
    : PythonDaemonSupervisor(
        new DaemonDescriptor(
            Name: "market replay",
            ScriptParts: new[] { "market_data", "replay", "run_replay.py" },
            ProcessMarker: "run_replay.py",
            PidSettingKey: SystemSettingKeys.ReplayPlayerPid,
            LogName: "market-replay"),
        engine, scopeFactory, logger);
