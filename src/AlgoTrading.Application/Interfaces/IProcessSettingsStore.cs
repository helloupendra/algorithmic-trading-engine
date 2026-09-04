// src/AlgoTrading.Application/Interfaces/IProcessSettingsStore.cs
namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Durable key/value store for the process ids of the Python children the API
/// launches (ingestor, strategy runners, backtest runners), kept in
/// system_settings so a restarted API can find and stop them again. Keys are
/// the constants / builders on <c>SystemSettingKeys</c>.
/// </summary>
public interface IProcessSettingsStore
{
    /// <summary>The raw value, or null when the key is absent.</summary>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>The value parsed as a positive process id, or null when absent / unparsable / not positive.</summary>
    Task<int?> GetPidAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Upserts the value. A write with an unchanged value is skipped.</summary>
    Task SetAsync(string key, string value, string? updatedBy = null, CancellationToken cancellationToken = default);

    /// <summary>Upserts a process id.</summary>
    Task SetPidAsync(string key, int processId, string? updatedBy = null, CancellationToken cancellationToken = default);

    /// <summary>Removes the key. Returns true when a row was deleted.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the key only when its current value is <paramref name="processId"/>,
    /// so a stale exit hook never clears the pid of a newer instance.
    /// </summary>
    Task<bool> DeleteIfPidAsync(string key, int processId, CancellationToken cancellationToken = default);
}
