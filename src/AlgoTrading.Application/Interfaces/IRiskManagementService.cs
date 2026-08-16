// src/AlgoTrading.Application/Interfaces/IRiskManagementService.cs
using System.Threading;
using System.Threading.Tasks;

namespace AlgoTrading.Application.Interfaces;

public interface IRiskManagementService
{
    Task EvaluateOrderAsync(long simulationRunId, string symbol, string side, int quantity, CancellationToken cancellationToken);
    
    Task ActivateKillSwitchAsync(CancellationToken cancellationToken);
    
    Task DeactivateKillSwitchAsync(CancellationToken cancellationToken);
    
    Task<bool> IsKillSwitchActiveAsync(CancellationToken cancellationToken);
}
