
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Application.UseCases.LiveData;

/// <summary>
/// Use case for retrieving the health and connectivity status of all active data ingestors.
/// </summary>
public class GetAllIngestorStatusesUseCase
{
    private readonly ILiveDataService _liveDataService;

    /// <summary>
    /// Initializes a new instance of <see cref="GetAllIngestorStatusesUseCase"/>.
    /// </summary>
    public GetAllIngestorStatusesUseCase(ILiveDataService liveDataService)
    {
        _liveDataService = liveDataService;
    }

    /// <summary>
    /// Fetches the status list from the live data service.
    /// </summary>
    public Task<IReadOnlyList<IngestorStatusResponse>> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        return _liveDataService.GetAllIngestorStatusesAsync(cancellationToken);
    }
}
