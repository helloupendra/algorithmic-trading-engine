// src/AlgoTrading.Infrastructure/Services/ProcessSettingsStore.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// system_settings-backed <see cref="IProcessSettingsStore"/>. No migration:
/// the table already exists for the kill switch; pids are just more rows.
/// Scoped (needs the DbContext); singletons reach it through a scope.
/// </summary>
public sealed class ProcessSettingsStore : IProcessSettingsStore
{
    private readonly TradingDbContext _dbContext;

    public ProcessSettingsStore(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        return await _dbContext.SystemSettings
            .AsNoTracking()
            .Where(x => x.Key == key)
            .Select(x => x.Value)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<int?> GetPidAsync(string key, CancellationToken cancellationToken = default)
    {
        var raw = await GetAsync(key, cancellationToken);
        return ParsePid(raw);
    }

    public async Task SetAsync(string key, string value, string? updatedBy = null, CancellationToken cancellationToken = default)
    {
        var setting = await _dbContext.SystemSettings
            .FirstOrDefaultAsync(x => x.Key == key, cancellationToken);

        var now = DateTime.UtcNow;
        if (setting is null)
        {
            await _dbContext.SystemSettings.AddAsync(new SystemSetting
            {
                Key = key,
                Value = value,
                UpdatedBy = updatedBy,
                CreatedUtc = now,
                UpdatedUtc = now
            }, cancellationToken);
        }
        else
        {
            if (string.Equals(setting.Value, value, StringComparison.Ordinal))
            {
                return;
            }

            setting.Value = value;
            setting.UpdatedBy = updatedBy;
            setting.UpdatedUtc = now;
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (setting is null)
        {
            // Lost an insert race on the unique key: the other writer's value
            // is as good as ours (both name the same live process); re-read and
            // overwrite only if it still differs. Any other insert failure
            // (connection dropped, constraint, disk) leaves no row behind —
            // the re-read finds nothing and the failure MUST propagate, or the
            // callers' best-effort warnings never fire and a live runner's pid
            // silently goes unrecorded (the next restart would then close and
            // flatten a run whose runner is still alive).
            _dbContext.ChangeTracker.Clear();
            var existing = await _dbContext.SystemSettings.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            if (!string.Equals(existing.Value, value, StringComparison.Ordinal))
            {
                existing.Value = value;
                existing.UpdatedBy = updatedBy;
                existing.UpdatedUtc = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }
    }

    public Task SetPidAsync(string key, int processId, string? updatedBy = null, CancellationToken cancellationToken = default)
        => SetAsync(key, processId.ToString(CultureInfo.InvariantCulture), updatedBy, cancellationToken);

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        int deleted = await _dbContext.SystemSettings
            .Where(x => x.Key == key)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    public async Task<bool> DeleteIfPidAsync(string key, int processId, CancellationToken cancellationToken = default)
    {
        var value = processId.ToString(CultureInfo.InvariantCulture);
        int deleted = await _dbContext.SystemSettings
            .Where(x => x.Key == key && x.Value == value)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }

    private static int? ParsePid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) && pid > 0
            ? pid
            : null;
    }
}
