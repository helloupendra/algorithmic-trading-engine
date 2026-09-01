
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Application.UseCases.LiveData;

/// <summary>
/// Use case for logging a heartbeat ping from a background worker to monitor its health.
/// </summary>
public class UpsertHeartbeatUseCase
{
    private readonly ILiveDataService _liveDataService;

    /// <summary>
    /// Initializes a new instance of <see cref="UpsertHeartbeatUseCase"/>.
    /// </summary>
    public UpsertHeartbeatUseCase(ILiveDataService liveDataService)
    {
        _liveDataService = liveDataService;
    }

    /// <summary>
    /// Records the heartbeat.
    /// </summary>
    public Task ExecuteAsync(
        UpsertHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        return _liveDataService.UpsertHeartbeatAsync(request, cancellationToken);
    }
}
