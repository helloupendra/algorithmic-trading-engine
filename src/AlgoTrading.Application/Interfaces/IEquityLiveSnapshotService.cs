// src/AlgoTrading.Application/Interfaces/IEquityLiveSnapshotService.cs
using AlgoTrading.Contracts.Equities;

namespace AlgoTrading.Application.Interfaces;

public interface IEquityLiveSnapshotService
{
    Task<EquityGroupLiveLatestResponse?> GetLatestByGroupAsync(
        string groupName,
        bool onlyEnabled = true,
        CancellationToken cancellationToken = default);
}
