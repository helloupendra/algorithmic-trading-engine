// src/AlgoTrading.Contracts/Strategies/RunnerRegistrationRequest.cs
namespace AlgoTrading.Contracts.Strategies;

/// <summary>
/// Body of POST /api/Strategy/runs/{runId}/runner: the execution runner
/// confirms its own process id once it knows its run id, so the API can find
/// (and stop) it again after an API restart.
/// </summary>
public class RunnerRegistrationRequest
{
    /// <summary>The runner's OS process id. Required, positive.</summary>
    public int ProcessId { get; set; }

    /// <summary>When the runner started (informational).</summary>
    public DateTime? StartedUtc { get; set; }
}

/// <summary>Response of POST /api/Strategy/runs/{runId}/runner.</summary>
public class RunnerRegistrationResponse
{
    public long RunId { get; set; }

    /// <summary>The pid now on record for the run.</summary>
    public int ProcessId { get; set; }

    /// <summary>True when the API launched this runner itself (the pid was already known).</summary>
    public bool Managed { get; set; }
}
