
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;

namespace AlgoTrading.Application.UseCases.LiveData;

/// <summary>
/// Use case for logging a heartbeat ping from a background worker to monitor its health.
/// A heartbeat that names the worker's process id also records it durably
/// (ingestor.pid), so an API that restarts can find and stop the ingestor again.
/// </summary>
public class UpsertHeartbeatUseCase
{
    private readonly ILiveDataService _liveDataService;
    private readonly IProcessSettingsStore _processSettings;

    /// <summary>
    /// Initializes a new instance of <see cref="UpsertHeartbeatUseCase"/>.
    /// </summary>
    public UpsertHeartbeatUseCase(ILiveDataService liveDataService, IProcessSettingsStore processSettings)
    {
        _liveDataService = liveDataService;
        _processSettings = processSettings;
    }

    /// <summary>
    /// Records the heartbeat (and the reporting process id when present).
    /// </summary>
    public async Task ExecuteAsync(
        UpsertHeartbeatRequest request,
        CancellationToken cancellationToken = default)
    {
        await _liveDataService.UpsertHeartbeatAsync(request, cancellationToken);

        if (request.ProcessId is > 0)
        {
            await _processSettings.SetPidAsync(
                SystemSettingKeys.IngestorPid, request.ProcessId.Value, updatedBy: request.SourceName, cancellationToken);
        }
    }
}
