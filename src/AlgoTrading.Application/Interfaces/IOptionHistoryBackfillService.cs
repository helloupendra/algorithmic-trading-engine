// src/AlgoTrading.Application/Interfaces/IOptionHistoryBackfillService.cs
using AlgoTrading.Contracts.Options;

namespace AlgoTrading.Application.Interfaces;

public interface IOptionHistoryBackfillService
{
    Task<BackfillOptionHistoryResponse> BackfillAsync(
        BackfillOptionHistoryRequest request,
        CancellationToken cancellationToken = default);
}
