using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;

namespace AlgoTrading.Application.UseCases.LiveData
{
    /// <summary>
    /// Use case for appending a batch of raw market ticks in one pass.
    /// </summary>
    /// <remarks>
    /// The batch sibling of <see cref="UpsertLiveTickUseCase"/>. The live ingestor
    /// posts through this one: storing ticks singly cost five database round-trips
    /// each, which put the writer level with the feed's own rate and let a backlog
    /// build that never drained.
    /// </remarks>
    public class UpsertLiveTicksUseCase
    {
        private readonly ILiveDataService _liveDataService;

        public UpsertLiveTicksUseCase(ILiveDataService liveDataService)
        {
            _liveDataService = liveDataService;
        }

        /// <summary>Appends every tick in the batch.</summary>
        public Task ExecuteAsync(
            IReadOnlyList<UpsertLiveTickRequest> requests,
            CancellationToken cancellationToken = default)
        {
            return _liveDataService.AppendLiveTicksAsync(requests, cancellationToken);
        }
    }
}
