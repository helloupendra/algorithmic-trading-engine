using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Application.UseCases.LiveData;

/// <summary>
/// Use case for retrieving the symbols currently tracked by the live data system.
/// </summary>
public class GetWatchlistUseCase
{
    private readonly ILiveDataService _liveDataService;

    /// <summary>
    /// Initializes a new instance of <see cref="GetWatchlistUseCase"/>.
    /// </summary>
    public GetWatchlistUseCase(ILiveDataService liveDataService)
    {
        _liveDataService = liveDataService;
    }

    /// <summary>
    /// Fetches the live watchlist.
    /// </summary>
    public Task<IReadOnlyList<LiveWatchlistItem>> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        return _liveDataService.GetWatchlistAsync(cancellationToken);
    }
}