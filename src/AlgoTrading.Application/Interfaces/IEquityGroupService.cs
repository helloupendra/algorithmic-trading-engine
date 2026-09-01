// src/AlgoTrading.Application/Interfaces/IEquityGroupService.cs
using AlgoTrading.Contracts.Equities;

namespace AlgoTrading.Application.Interfaces;

public interface IEquityGroupService
{
    Task<IReadOnlyList<EquityGroupResponse>> GetGroupsAsync(
        bool onlyEnabled = true,
        CancellationToken cancellationToken = default);

    Task<EquityGroupResponse?> GetGroupByNameAsync(
        string groupName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EquityGroupMemberResponse>> GetMembersAsync(
        string groupName,
        bool onlyEnabled = true,
        CancellationToken cancellationToken = default);

    Task<EquityGroupResponse> CreateGroupAsync(
        CreateEquityGroupRequest request,
        CancellationToken cancellationToken = default);

    Task<EquityGroupMemberResponse> AddMemberAsync(
        string groupName,
        AddEquityGroupMemberRequest request,
        CancellationToken cancellationToken = default);
}