using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Risk;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Config;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Services;

public class RiskLimitsStore : IRiskLimitsStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RiskLimitsStore> _logger;
    private readonly RiskManagementSettings _defaultSettings;

    // Cache to match the 2-second cache killswitch pattern
    private RiskLimitsDto? _cachedLimits;
    private DateTime _lastCacheUpdate = DateTime.MinValue;
    private readonly object _lock = new object();
    private readonly TimeSpan _cacheDuration = TimeSpan.FromSeconds(2);

    public RiskLimitsStore(
        IServiceScopeFactory scopeFactory,
        ILogger<RiskLimitsStore> logger,
        IOptions<RiskManagementSettings> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _defaultSettings = options.Value;
    }

    public RiskLimitsDto GetLimits()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _lastCacheUpdate < _cacheDuration && _cachedLimits != null)
            {
                return _cachedLimits;
            }
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            var keys = new[]
            {
                SystemSettingKeys.MaxOrdersPerMinute,
                SystemSettingKeys.MaxDailyLoss,
                SystemSettingKeys.MaxConcurrentRuns,
                SystemSettingKeys.MaxRunsPerUser
            };

            var settings = dbContext.SystemSettings
                .Where(s => keys.Contains(s.Key))
                .ToList();

            var maxOrders = GetInt(settings, SystemSettingKeys.MaxOrdersPerMinute, _defaultSettings.MaxOrdersPerMinute);
            var maxLoss = GetDecimal(settings, SystemSettingKeys.MaxDailyLoss, _defaultSettings.MaxDailyLoss);
            
            // Assume default concurrent runs = 10, runs per user = 5
            var maxConcurrent = GetInt(settings, SystemSettingKeys.MaxConcurrentRuns, 10);
            var maxPerUser = GetInt(settings, SystemSettingKeys.MaxRunsPerUser, 5);

            var latestUpdate = settings.Count > 0 ? settings.Max(s => s.UpdatedUtc) : (DateTime?)null;
            var latestUpdater = settings.OrderByDescending(s => s.UpdatedUtc).FirstOrDefault()?.UpdatedBy;
            var source = settings.Count > 0 ? "database" : "config";

            var limits = new RiskLimitsDto
            {
                MaxOrdersPerMinute = maxOrders,
                MaxDailyLoss = maxLoss,
                MaxConcurrentRuns = maxConcurrent,
                MaxRunsPerUser = maxPerUser,
                Source = source,
                UpdatedBy = latestUpdater,
                UpdatedUtc = latestUpdate
            };

            lock (_lock)
            {
                _cachedLimits = limits;
                _lastCacheUpdate = DateTime.UtcNow;
            }

            return limits;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read risk limits from database. Using last known or defaults.");
            
            lock (_lock)
            {
                if (_cachedLimits != null)
                {
                    return _cachedLimits;
                }
            }

            return new RiskLimitsDto
            {
                MaxOrdersPerMinute = _defaultSettings.MaxOrdersPerMinute,
                MaxDailyLoss = _defaultSettings.MaxDailyLoss,
                MaxConcurrentRuns = 10,
                MaxRunsPerUser = 5,
                Source = "config"
            };
        }
    }

    public async Task UpdateLimitsAsync(RiskLimitsDto newLimits, string updatedBy, CancellationToken cancellationToken)
    {
        if (newLimits.MaxOrdersPerMinute < 1 || newLimits.MaxOrdersPerMinute > 10000)
            throw new ArgumentOutOfRangeException(nameof(newLimits.MaxOrdersPerMinute), "Must be between 1 and 10000.");
            
        if (newLimits.MaxDailyLoss > 0)
            throw new ArgumentOutOfRangeException(nameof(newLimits.MaxDailyLoss), "Must be <= 0 (negative).");
            
        if (newLimits.MaxConcurrentRuns < 1 || newLimits.MaxConcurrentRuns > 50)
            throw new ArgumentOutOfRangeException(nameof(newLimits.MaxConcurrentRuns), "Must be between 1 and 50.");
            
        if (newLimits.MaxRunsPerUser < 1 || newLimits.MaxRunsPerUser > 50)
            throw new ArgumentOutOfRangeException(nameof(newLimits.MaxRunsPerUser), "Must be between 1 and 50.");

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        await UpsertSetting(dbContext, SystemSettingKeys.MaxOrdersPerMinute, newLimits.MaxOrdersPerMinute.ToString(), updatedBy, cancellationToken);
        await UpsertSetting(dbContext, SystemSettingKeys.MaxDailyLoss, newLimits.MaxDailyLoss.ToString(), updatedBy, cancellationToken);
        await UpsertSetting(dbContext, SystemSettingKeys.MaxConcurrentRuns, newLimits.MaxConcurrentRuns.ToString(), updatedBy, cancellationToken);
        await UpsertSetting(dbContext, SystemSettingKeys.MaxRunsPerUser, newLimits.MaxRunsPerUser.ToString(), updatedBy, cancellationToken);

        var evt = new RiskEvent
        {
            OccurredUtc = DateTime.UtcNow,
            Kind = "LimitsChanged",
            ActorName = updatedBy,
            Reason = "Admin update",
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(newLimits)
        };
        dbContext.RiskEvents.Add(evt);

        await dbContext.SaveChangesAsync(cancellationToken);

        newLimits.Source = "database";
        newLimits.UpdatedBy = updatedBy;
        newLimits.UpdatedUtc = DateTime.UtcNow;

        lock (_lock)
        {
            _cachedLimits = newLimits;
            _lastCacheUpdate = DateTime.UtcNow;
        }
    }

    private int GetInt(List<SystemSetting> settings, string key, int defaultValue)
    {
        var setting = settings.FirstOrDefault(s => s.Key == key);
        if (setting != null && int.TryParse(setting.Value, out int result))
        {
            return result;
        }
        return defaultValue;
    }

    private decimal GetDecimal(List<SystemSetting> settings, string key, decimal defaultValue)
    {
        var setting = settings.FirstOrDefault(s => s.Key == key);
        if (setting != null && decimal.TryParse(setting.Value, out decimal result))
        {
            return result;
        }
        return defaultValue;
    }

    private async Task UpsertSetting(TradingDbContext dbContext, string key, string value, string updatedBy, CancellationToken ct)
    {
        var setting = await dbContext.SystemSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting == null)
        {
            setting = new SystemSetting
            {
                Key = key,
                Value = value,
                UpdatedBy = updatedBy,
                CreatedUtc = DateTime.UtcNow,
                UpdatedUtc = DateTime.UtcNow
            };
            dbContext.SystemSettings.Add(setting);
        }
        else
        {
            setting.Value = value;
            setting.UpdatedBy = updatedBy;
            setting.UpdatedUtc = DateTime.UtcNow;
        }
    }
}
