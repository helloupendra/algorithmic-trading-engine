using AlgoTrading.Contracts.Risk;

namespace AlgoTrading.Application.Interfaces;

public interface IRiskLimitsStore
{
    RiskLimitsDto GetLimits();
    Task UpdateLimitsAsync(RiskLimitsDto newLimits, string updatedBy, CancellationToken cancellationToken);
}
