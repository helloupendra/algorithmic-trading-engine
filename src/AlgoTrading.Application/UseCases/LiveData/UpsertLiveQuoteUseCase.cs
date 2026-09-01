using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Application.UseCases.LiveData;

/// <summary>
/// Use case for updating the latest known price snapshot for a symbol in the database.
/// </summary>
public class UpsertLiveQuoteUseCase
{
    private readonly ILiveDataService _liveDataService;

    /// <summary>
    /// Initializes a new instance of <see cref="UpsertLiveQuoteUseCase"/>.
    /// </summary>
    public UpsertLiveQuoteUseCase(ILiveDataService liveDataService)
    {
        _liveDataService = liveDataService;
    }

    /// <summary>
    /// Upserts the live quote.
    /// </summary>
    public Task ExecuteAsync(
        UpsertLiveQuoteRequest request,
        CancellationToken cancellationToken = default)
    {
        return _liveDataService.UpsertLatestQuoteAsync(request, cancellationToken);
    }
}