using System.Text.Json;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.SeedData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Services;

public class ReferenceDataSeeder
{
    private readonly TradingDbContext _dbContext;
    private readonly IHostEnvironment _hostEnvironment;
    private readonly ILogger<ReferenceDataSeeder> _logger;

    public ReferenceDataSeeder(
        TradingDbContext dbContext,
        IHostEnvironment hostEnvironment,
        ILogger<ReferenceDataSeeder> logger)
    {
        _dbContext = dbContext;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await SeedUsersAsync(cancellationToken);
        await SeedStrategiesAsync(cancellationToken);
        await SeedLiveWatchlistAsync(cancellationToken);
        await SeedEquityGroupsAsync(cancellationToken);
        await SeedEquityGroupMembersAsync(cancellationToken);
        await SeedMarketCalendarAsync(cancellationToken);
    }

    /// <summary>
    /// The shipped holiday calendar (<c>SeedData/market_calendar.json</c>, one
    /// block per exchange circular).
    /// </summary>
    /// <remarks>
    /// A year is seeded once per exchange. After that the table is the truth: an
    /// admin's correction or deletion (a holiday the exchange cancelled) must
    /// survive every restart, so a year that already has rows is left alone.
    /// Special sessions are announced later in the year than holidays, so they
    /// are added by date whenever the file gains one.
    /// </remarks>
    internal async Task SeedMarketCalendarAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(_hostEnvironment.ContentRootPath, "SeedData", "market_calendar.json");
        if (!File.Exists(path))
        {
            _logger.LogWarning("Seed file not found: {Path}. Session checks will know weekends only until a calendar is added.", path);
            return;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var file = JsonSerializer.Deserialize<MarketCalendarSeedFile>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new MarketCalendarSeedFile();

        var existingHolidays = await _dbContext.MarketHolidays.AsNoTracking()
            .Select(x => new { x.Exchange, x.Date })
            .ToListAsync(cancellationToken);
        var seededYears = existingHolidays.Select(x => (x.Exchange.ToUpperInvariant(), x.Date.Year)).ToHashSet();

        int addedHolidays = 0;
        var now = DateTime.UtcNow;
        foreach (var item in file.Holidays ?? new())
        {
            var exchange = MarketCalendar.NormalizeExchange(item.Exchange);
            if (!DateOnly.TryParseExact(item.Date, "yyyy-MM-dd", out var date) ||
                !Enum.TryParse<Domain.Enums.MarketClosure>(item.Closure ?? "FullDay", ignoreCase: true, out var closure) ||
                string.IsNullOrWhiteSpace(item.Name))
            {
                _logger.LogWarning("Skipped an unreadable market holiday in the seed file: {Exchange} {Date} {Name}.", item.Exchange, item.Date, item.Name);
                continue;
            }

            if (seededYears.Contains((exchange, date.Year))) continue;

            _dbContext.MarketHolidays.Add(new MarketHoliday
            {
                Exchange = exchange,
                Date = date,
                Name = item.Name.Trim(),
                Closure = closure,
                Source = item.Source,
                UpdatedBy = "seed",
                CreatedUtc = now,
                UpdatedUtc = now,
            });
            addedHolidays++;
        }

        var existingSessions = await _dbContext.MarketSpecialSessions.AsNoTracking()
            .Select(x => new { x.Exchange, x.Date })
            .ToListAsync(cancellationToken);
        var sessionDays = existingSessions.Select(x => (x.Exchange.ToUpperInvariant(), x.Date)).ToHashSet();

        int addedSessions = 0;
        foreach (var item in file.SpecialSessions ?? new())
        {
            var exchange = MarketCalendar.NormalizeExchange(item.Exchange);
            if (!DateOnly.TryParseExact(item.Date, "yyyy-MM-dd", out var date) ||
                !TimeOnly.TryParseExact(item.OpenIst, "HH:mm", out var open) ||
                !TimeOnly.TryParseExact(item.CloseIst, "HH:mm", out var close) ||
                close <= open || string.IsNullOrWhiteSpace(item.Name))
            {
                _logger.LogWarning("Skipped an unreadable special session in the seed file: {Exchange} {Date} {Name}.", item.Exchange, item.Date, item.Name);
                continue;
            }

            if (!sessionDays.Add((exchange, date))) continue;

            _dbContext.MarketSpecialSessions.Add(new MarketSpecialSession
            {
                Exchange = exchange,
                Date = date,
                Name = item.Name.Trim(),
                OpenIst = open,
                CloseIst = close,
                Source = item.Source,
                UpdatedBy = "seed",
                CreatedUtc = now,
                UpdatedUtc = now,
            });
            addedSessions++;
        }

        if (addedHolidays + addedSessions > 0)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Seeded {Holidays} market holiday(s) and {Sessions} special session(s).", addedHolidays, addedSessions);
        }
    }

    private sealed class MarketCalendarSeedFile
    {
        public List<HolidaySeedItem>? Holidays { get; set; }
        public List<SpecialSessionSeedItem>? SpecialSessions { get; set; }
    }

    private sealed class HolidaySeedItem
    {
        public string Exchange { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Closure { get; set; }
        public string? Source { get; set; }
    }

    private sealed class SpecialSessionSeedItem
    {
        public string Exchange { get; set; } = string.Empty;
        public string Date { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string OpenIst { get; set; } = string.Empty;
        public string CloseIst { get; set; } = string.Empty;
        public string? Source { get; set; }
    }

    private async Task SeedUsersAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(
            _hostEnvironment.ContentRootPath,
            "SeedData",
            "users.json");

        if (!File.Exists(path))
        {
            _logger.LogWarning("Seed file not found: {Path}", path);
            return;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var items = JsonSerializer.Deserialize<List<UserSeedItem>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

        foreach (var item in items)
        {
            var username = item.Username.Trim().ToLowerInvariant();
            var existing = await _dbContext.AppUsers
                .FirstOrDefaultAsync(x => x.UserName.ToLower() == username, cancellationToken);

            if (existing is null)
            {
                await _dbContext.AppUsers.AddAsync(new AppUser
                {
                    UserName = item.Username,
                    Email = item.Email,
                    TotalCapital = item.TotalCapital,
                    // Unrecognised or omitted roles fall back to Trader, so a seed
                    // file typo can never create an administrator.
                    Role = Domain.Constants.UserRoles.Normalize(item.Role)
                           ?? Domain.Constants.UserRoles.Trader,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow,
                    IsActive = true
                }, cancellationToken);

                _logger.LogWarning(
                    "Seeded user '{UserName}' has no password and cannot sign in. " +
                    "Set one from the admin panel before using this account.",
                    item.Username);
            }
            else
            {
                existing.Email = item.Email;
                existing.TotalCapital = item.TotalCapital;
                existing.UpdatedUtc = DateTime.UtcNow;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedStrategiesAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(
            _hostEnvironment.ContentRootPath,
            "SeedData",
            "strategies.json");

        if (!File.Exists(path))
        {
            _logger.LogWarning("Seed file not found: {Path}", path);
            return;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var items = JsonSerializer.Deserialize<List<StrategySeedItem>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

        foreach (var item in items)
        {
            var name = item.Name.Trim();
            var existing = await _dbContext.Strategies
                .FirstOrDefaultAsync(x => x.Name == name, cancellationToken);

            if (existing is null)
            {
                await _dbContext.Strategies.AddAsync(new StrategyDefinition
                {
                    Name = item.Name,
                    Description = item.Description,
                    DefaultParametersJson = item.DefaultParametersJson,
                    CreatedUtc = DateTime.UtcNow
                }, cancellationToken);
            }
            else
            {
                existing.Description = item.Description;
                existing.DefaultParametersJson = item.DefaultParametersJson;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedLiveWatchlistAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(
            _hostEnvironment.ContentRootPath,
            "SeedData",
            "live_watchlist.json");

        if (!File.Exists(path))
        {
            _logger.LogWarning("Seed file not found: {Path}", path);
            return;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var items = JsonSerializer.Deserialize<List<LiveWatchlistSeedItem>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

        foreach (var item in items)
        {
            var symbol = item.Symbol.Trim().ToUpperInvariant();
            var existing = await _dbContext.LiveWatchlistItems
                .FirstOrDefaultAsync(x => x.Symbol == symbol, cancellationToken);

            if (existing is null)
            {
                await _dbContext.LiveWatchlistItems.AddAsync(new LiveWatchlistItem
                {
                    Symbol = symbol,
                    DataType = item.DataType,
                    IsActive = item.IsActive,
                    Priority = item.Priority,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                }, cancellationToken);
            }
            else
            {
                existing.DataType = item.DataType;
                existing.IsActive = item.IsActive;
                existing.Priority = item.Priority;
                existing.UpdatedUtc = DateTime.UtcNow;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedEquityGroupsAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(
            _hostEnvironment.ContentRootPath,
            "SeedData",
            "equity_groups.json");

        if (!File.Exists(path))
        {
            _logger.LogWarning("Seed file not found: {Path}", path);
            return;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var items = JsonSerializer.Deserialize<List<EquityGroupSeedItem>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

        foreach (var item in items)
        {
            string name = item.Name.Trim().ToUpperInvariant();
            string exchange = item.Exchange.Trim().ToUpperInvariant();

            var existing = await _dbContext.EquityGroups
                .FirstOrDefaultAsync(x => x.Name == name, cancellationToken);

            if (existing is null)
            {
                await _dbContext.EquityGroups.AddAsync(new EquityGroup
                {
                    Name = name,
                    Exchange = exchange,
                    DisplayName = item.DisplayName,
                    Description = item.Description,
                    IsEnabled = item.IsEnabled,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                }, cancellationToken);
            }
            else
            {
                existing.Exchange = exchange;
                existing.DisplayName = item.DisplayName;
                existing.Description = item.Description;
                existing.IsEnabled = item.IsEnabled;
                existing.UpdatedUtc = DateTime.UtcNow;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedEquityGroupMembersAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(
            _hostEnvironment.ContentRootPath,
            "SeedData",
            "equity_group_members.json");

        if (!File.Exists(path))
        {
            _logger.LogWarning("Seed file not found: {Path}", path);
            return;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var items = JsonSerializer.Deserialize<List<EquityGroupMemberSeedItem>>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

        var groups = await _dbContext.EquityGroups
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Name, x => x.Id, cancellationToken);

        foreach (var item in items)
        {
            string groupName = item.GroupName.Trim().ToUpperInvariant();

            if (!groups.TryGetValue(groupName, out var groupId))
            {
                _logger.LogWarning("Skipping member seed because group '{GroupName}' does not exist.", groupName);
                continue;
            }

            string symbol = item.Symbol.Trim().ToUpperInvariant();

            var existing = await _dbContext.EquityGroupMembers.FirstOrDefaultAsync(x =>
                x.EquityGroupId == groupId &&
                x.Symbol == symbol &&
                x.EffectiveFrom == item.EffectiveFrom &&
                x.EffectiveTo == item.EffectiveTo,
                cancellationToken);

            if (existing is null)
            {
                await _dbContext.EquityGroupMembers.AddAsync(new EquityGroupMember
                {
                    EquityGroupId = groupId,
                    Symbol = symbol,
                    Weight = item.Weight,
                    EffectiveFrom = item.EffectiveFrom,
                    EffectiveTo = item.EffectiveTo,
                    IsEnabled = item.IsEnabled,
                    CreatedUtc = DateTime.UtcNow,
                    UpdatedUtc = DateTime.UtcNow
                }, cancellationToken);
            }
            else
            {
                existing.Weight = item.Weight;
                existing.IsEnabled = item.IsEnabled;
                existing.UpdatedUtc = DateTime.UtcNow;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}