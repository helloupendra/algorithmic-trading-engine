
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Application.UseCases.LiveData;

/// <summary>
/// Use case for identifying active symbols that have stopped receiving live data updates.
/// </summary>
public class GetStaleQuotesUseCase
{
    private readonly ILiveDataService _liveDataService;

    /// <summary>
    /// Initializes a new instance of <see cref="GetStaleQuotesUseCase"/>.
    /// </summary>
    public GetStaleQuotesUseCase(ILiveDataService liveDataService)
    {
        _liveDataService = liveDataService;
    }

    /// <summary>
    /// Fetches a list of quotes that are considered stale based on the threshold.
    /// </summary>
    public Task<IReadOnlyList<StaleQuoteResponse>> ExecuteAsync(
        int staleAfterSeconds,
        CancellationToken cancellationToken = default)
    {
        return _liveDataService.GetStaleQuotesAsync(staleAfterSeconds, cancellationToken);
    }
}
