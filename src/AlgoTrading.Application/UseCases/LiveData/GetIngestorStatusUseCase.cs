
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Application.UseCases.LiveData;

/// <summary>
/// Use case for retrieving the health status of a specific background data ingestor.
/// </summary>
public class GetIngestorStatusUseCase
{
    private readonly ILiveDataService _liveDataService;

    /// <summary>
    /// Initializes a new instance of <see cref="GetIngestorStatusUseCase"/>.
    /// </summary>
    public GetIngestorStatusUseCase(ILiveDataService liveDataService)
    {
        _liveDataService = liveDataService;
    }

    /// <summary>
    /// Fetches the status of the specified ingestor.
    /// </summary>
    public Task<IngestorStatusResponse?> ExecuteAsync(
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        return _liveDataService.GetIngestorStatusAsync(sourceName, cancellationToken);
    }
}
