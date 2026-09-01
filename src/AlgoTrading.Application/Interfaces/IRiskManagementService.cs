// src/AlgoTrading.Application/Interfaces/IRiskManagementService.cs
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Who set the kill switch, when, and why — surfaced on the admin panel so an
/// operator can see the reason a halt is in force before lifting it.
/// </summary>
public class KillSwitchState
{
    public bool IsActive { get; set; }
    public string? UpdatedBy { get; set; }
    public string? Reason { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

public interface IRiskManagementService
{
    Task EvaluateOrderAsync(long simulationRunId, string symbol, string side, int quantity, CancellationToken cancellationToken);

    Task ActivateKillSwitchAsync(CancellationToken cancellationToken);

    Task DeactivateKillSwitchAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Halts trading platform-wide and records who did it and why.
    /// The flag is persisted, so it survives an API restart.
    /// </summary>
    Task ActivateKillSwitchAsync(string? updatedBy, string? reason, CancellationToken cancellationToken);

    /// <summary>
    /// Resumes trading and records who lifted the halt and why.
    /// </summary>
    Task DeactivateKillSwitchAsync(string? updatedBy, string? reason, CancellationToken cancellationToken);

    Task<bool> IsKillSwitchActiveAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Full kill-switch state including the audit fields.
    /// </summary>
    Task<KillSwitchState> GetKillSwitchStateAsync(CancellationToken cancellationToken);
}
