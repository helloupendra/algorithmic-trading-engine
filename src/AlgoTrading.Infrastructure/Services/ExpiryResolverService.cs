// src/AlgoTrading.Infrastructure/Services/ExpiryResolverService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Expiry;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Domain.Enums;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

public class ExpiryResolverService : IExpiryResolverService
{
    private readonly TradingDbContext _dbContext;

    public ExpiryResolverService(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<ExpiryRuleResponse?> GetRuleAsync(
        string exchange,
        string underlying,
        CancellationToken cancellationToken = default)
    {
        var rule = await _dbContext.ExpiryRules
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.IsEnabled &&
                     x.Exchange == exchange &&
                     x.Underlying == underlying,
                cancellationToken);

        if (rule is null)
            return null;

        return MapRule(rule);
    }

    public async Task<IReadOnlyList<DateOnly>> GetAvailableExpiriesAsync(
        string exchange,
        string underlying,
        CancellationToken cancellationToken = default)
    {
        var dates = await _dbContext.Instruments
            .AsNoTracking()
            .Where(x =>
                x.IsEnabled &&
                x.Exchange == exchange &&
                x.Underlying == underlying &&
                x.ExpiryDate.HasValue)
            .Select(x => x.ExpiryDate!.Value)
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(cancellationToken);

        return dates;
    }

    public async Task<ResolvedExpiryResponse?> ResolvePreferredExpiryAsync(
        string exchange,
        string underlying,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        var rule = await _dbContext.ExpiryRules
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.IsEnabled &&
                     x.Exchange == exchange &&
                     x.Underlying == underlying,
                cancellationToken);

        if (rule is null)
            return null;

        return await ResolveByTypeInternalAsync(
            exchange,
            underlying,
            rule.PreferredExpiryType,
            utcNow,
            cancellationToken);
    }

    public async Task<ResolvedExpiryResponse?> ResolveExactExpiryAsync(
        string exchange,
        string underlying,
        string expiryType,
        DateTime utcNow,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<ExpiryType>(expiryType, true, out var parsedType))
            throw new InvalidOperationException($"Unsupported expiry type '{expiryType}'.");

        return await ResolveByTypeInternalAsync(
            exchange,
            underlying,
            parsedType,
            utcNow,
            cancellationToken);
    }

    private async Task<ResolvedExpiryResponse?> ResolveByTypeInternalAsync(
        string exchange,
        string underlying,
        ExpiryType expiryType,
        DateTime utcNow,
        CancellationToken cancellationToken)
    {
        var expiries = await GetAvailableExpiriesAsync(exchange, underlying, cancellationToken);

        if (expiries.Count == 0)
            return null;

        var indiaToday = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTimeFromUtc(utcNow, GetIndiaTimeZone()).Date);

        var upcoming = expiries.Where(x => x >= indiaToday).OrderBy(x => x).ToList();

        if (upcoming.Count == 0)
            return null;

        // First version:
        // pick the next available expiry from instruments.
        // Later we can split true weekly/monthly/quarterly classification if needed.
        var next = upcoming.First();

        return new ResolvedExpiryResponse
        {
            Exchange = exchange,
            Underlying = underlying,
            ExpiryType = expiryType.ToString(),
            ExpiryDate = next,
            IsHolidayAdjusted = false,
            ResolutionSource = "Instruments"
        };
    }

    private static ExpiryRuleResponse MapRule(ExpiryRule rule)
    {
        return new ExpiryRuleResponse
        {
            Exchange = rule.Exchange,
            Underlying = rule.Underlying,
            HasWeekly = rule.HasWeekly,
            HasMonthly = rule.HasMonthly,
            HasQuarterly = rule.HasQuarterly,
            HasSemiAnnual = rule.HasSemiAnnual,
            WeeklyExpiryDay = rule.WeeklyExpiryDay?.ToString(),
            MonthlyExpiryDay = rule.MonthlyExpiryDay?.ToString(),
            QuarterlyExpiryDay = rule.QuarterlyExpiryDay?.ToString(),
            SemiAnnualExpiryDay = rule.SemiAnnualExpiryDay?.ToString(),
            HolidayShiftRule = rule.HolidayShiftRule.ToString(),
            PreferredExpiryType = rule.PreferredExpiryType.ToString()
        };
    }

    private static TimeZoneInfo GetIndiaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        }
    }
}