// src/AlgoTrading.Infrastructure/Services/EquityLiveSnapshotService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Equities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

public class EquityLiveSnapshotService : IEquityLiveSnapshotService
{
    private readonly TradingDbContext _dbContext;

    public EquityLiveSnapshotService(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<EquityGroupLiveLatestResponse?> GetLatestByGroupAsync(
        string groupName,
        bool onlyEnabled = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            throw new InvalidOperationException("groupName is required.");

        string normalizedGroupName = groupName.Trim().ToUpperInvariant();

        var group = await _dbContext.EquityGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name == normalizedGroupName, cancellationToken);

        if (group is null)
            return null;

        var memberQuery = _dbContext.EquityGroupMembers
            .AsNoTracking()
            .Where(x => x.EquityGroupId == group.Id);

        if (onlyEnabled)
        {
            memberQuery = memberQuery.Where(x => x.IsEnabled);
        }

        var members = await memberQuery
            .OrderByDescending(x => x.Weight)
            .ThenBy(x => x.Symbol)
            .ToListAsync(cancellationToken);

        if (members.Count == 0)
        {
            return new EquityGroupLiveLatestResponse
            {
                GroupName = group.Name,
                Exchange = group.Exchange,
                DisplayName = group.DisplayName,
                TotalMembers = 0,
                MembersWithLiveData = 0,
                Members = new List<EquityLatestQuoteResponse>()
            };
        }

        var symbols = members
            .Select(x => x.Symbol.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();

        var latestQuotes = await _dbContext.LiveQuotesLatest
            .AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol))
            .ToListAsync(cancellationToken);

        var latestBySymbol = latestQuotes
            .GroupBy(x => x.Symbol)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.UpdatedUtc).First());

        var resultMembers = new List<EquityLatestQuoteResponse>();

        foreach (var member in members)
        {
            string symbol = member.Symbol.Trim().ToUpperInvariant();

            if (latestBySymbol.TryGetValue(symbol, out var quote))
            {
                resultMembers.Add(new EquityLatestQuoteResponse
                {
                    Symbol = symbol,
                    Weight = member.Weight,
                    LastTradedPrice = quote.LastTradedPrice,
                    Open = quote.Open,
                    High = quote.High,
                    Low = quote.Low,
                    Close = quote.Close,
                    Volume = quote.Volume,
                    UpdatedUtc = quote.UpdatedUtc,
                    HasLiveData = true
                });
            }
            else
            {
                resultMembers.Add(new EquityLatestQuoteResponse
                {
                    Symbol = symbol,
                    Weight = member.Weight,
                    LastTradedPrice = null,
                    Open = null,
                    High = null,
                    Low = null,
                    Close = null,
                    Volume = null,
                    UpdatedUtc = null,
                    HasLiveData = false
                });
            }
        }

        return new EquityGroupLiveLatestResponse
        {
            GroupName = group.Name,
            Exchange = group.Exchange,
            DisplayName = group.DisplayName,
            TotalMembers = members.Count,
            MembersWithLiveData = resultMembers.Count(x => x.HasLiveData),
            Members = resultMembers
        };
    }
}