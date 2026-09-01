// src/AlgoTrading.Infrastructure/Services/EquityGroupService.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Equities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

public class EquityGroupService : IEquityGroupService
{
    private readonly TradingDbContext _dbContext;

    public EquityGroupService(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<EquityGroupResponse>> GetGroupsAsync(
        bool onlyEnabled = true,
        CancellationToken cancellationToken = default)
    {
        var query = _dbContext.EquityGroups
            .AsNoTracking()
            .AsQueryable();

        if (onlyEnabled)
        {
            query = query.Where(x => x.IsEnabled);
        }

        var groups = await query
            .OrderBy(x => x.Exchange)
            .ThenBy(x => x.DisplayName)
            .Select(x => new EquityGroupResponse
            {
                Id = x.Id,
                Name = x.Name,
                Exchange = x.Exchange,
                DisplayName = x.DisplayName,
                Description = x.Description,
                IsEnabled = x.IsEnabled,
                MemberCount = x.Members.Count(m => m.IsEnabled)
            })
            .ToListAsync(cancellationToken);

        return groups;
    }

    public async Task<EquityGroupResponse?> GetGroupByNameAsync(
        string groupName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            throw new InvalidOperationException("groupName is required.");

        string normalized = groupName.Trim().ToUpperInvariant();

        var group = await _dbContext.EquityGroups
            .AsNoTracking()
            .Where(x => x.Name == normalized)
            .Select(x => new EquityGroupResponse
            {
                Id = x.Id,
                Name = x.Name,
                Exchange = x.Exchange,
                DisplayName = x.DisplayName,
                Description = x.Description,
                IsEnabled = x.IsEnabled,
                MemberCount = x.Members.Count(m => m.IsEnabled)
            })
            .FirstOrDefaultAsync(cancellationToken);

        return group;
    }

    public async Task<IReadOnlyList<EquityGroupMemberResponse>> GetMembersAsync(
        string groupName,
        bool onlyEnabled = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            throw new InvalidOperationException("groupName is required.");

        string normalized = groupName.Trim().ToUpperInvariant();

        var group = await _dbContext.EquityGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Name == normalized, cancellationToken);

        if (group is null)
            return Array.Empty<EquityGroupMemberResponse>();

        var query = _dbContext.EquityGroupMembers
            .AsNoTracking()
            .Where(x => x.EquityGroupId == group.Id)
            .AsQueryable();

        if (onlyEnabled)
        {
            query = query.Where(x => x.IsEnabled);
        }

        var rows = await query
            .OrderByDescending(x => x.Weight)
            .ThenBy(x => x.Symbol)
            .Select(x => new EquityGroupMemberResponse
            {
                Id = x.Id,
                EquityGroupId = x.EquityGroupId,
                Symbol = x.Symbol,
                Weight = x.Weight,
                EffectiveFrom = x.EffectiveFrom,
                EffectiveTo = x.EffectiveTo,
                IsEnabled = x.IsEnabled
            })
            .ToListAsync(cancellationToken);

        return rows;
    }

    public async Task<EquityGroupResponse> CreateGroupAsync(
        CreateEquityGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        string normalizedName = request.Name.Trim().ToUpperInvariant();

        bool exists = await _dbContext.EquityGroups.AnyAsync(x => x.Name == normalizedName, cancellationToken);
        if (exists)
            throw new InvalidOperationException($"Equity group '{normalizedName}' already exists.");

        var group = new AlgoTrading.Domain.Entities.EquityGroup
        {
            Name = normalizedName,
            Exchange = request.Exchange.Trim().ToUpperInvariant(),
            DisplayName = request.DisplayName,
            Description = request.Description,
            IsEnabled = true,
            CreatedUtc = DateTime.UtcNow
        };

        _dbContext.EquityGroups.Add(group);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new EquityGroupResponse
        {
            Id = group.Id,
            Name = group.Name,
            Exchange = group.Exchange,
            DisplayName = group.DisplayName,
            Description = group.Description,
            IsEnabled = group.IsEnabled,
            MemberCount = 0
        };
    }

    public async Task<EquityGroupMemberResponse> AddMemberAsync(
        string groupName,
        AddEquityGroupMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            throw new InvalidOperationException("groupName is required.");

        string normalizedName = groupName.Trim().ToUpperInvariant();

        var group = await _dbContext.EquityGroups.FirstOrDefaultAsync(x => x.Name == normalizedName, cancellationToken);
        if (group is null)
            throw new InvalidOperationException($"Equity group '{normalizedName}' not found.");

        string normalizedSymbol = request.Symbol.Trim().ToUpperInvariant();

        bool memberExists = await _dbContext.EquityGroupMembers
            .AnyAsync(x => x.EquityGroupId == group.Id && x.Symbol == normalizedSymbol, cancellationToken);

        if (memberExists)
            throw new InvalidOperationException($"Symbol '{normalizedSymbol}' is already in group '{normalizedName}'.");

        var member = new AlgoTrading.Domain.Entities.EquityGroupMember
        {
            EquityGroupId = group.Id,
            Symbol = normalizedSymbol,
            Weight = request.Weight,
            EffectiveFrom = DateOnly.FromDateTime(DateTime.UtcNow),
            IsEnabled = true,
            CreatedUtc = DateTime.UtcNow
        };

        _dbContext.EquityGroupMembers.Add(member);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new EquityGroupMemberResponse
        {
            Id = member.Id,
            EquityGroupId = member.EquityGroupId,
            Symbol = member.Symbol,
            Weight = member.Weight,
            EffectiveFrom = member.EffectiveFrom,
            EffectiveTo = member.EffectiveTo,
            IsEnabled = member.IsEnabled
        };
    }
}