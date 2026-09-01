// src/AlgoTrading.Application/UseCases/Equities/AddEquityGroupToWatchlistUseCase.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Equities;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Application.UseCases.LiveData;

namespace AlgoTrading.Application.UseCases.Equities;

public class AddEquityGroupToWatchlistUseCase
{
    private readonly IEquityGroupService _equityGroupService;
    private readonly UpsertWatchlistItemUseCase _upsertWatchlistItemUseCase;

    public AddEquityGroupToWatchlistUseCase(
        IEquityGroupService equityGroupService,
        UpsertWatchlistItemUseCase upsertWatchlistItemUseCase)
    {
        _equityGroupService = equityGroupService;
        _upsertWatchlistItemUseCase = upsertWatchlistItemUseCase;
    }

    public async Task<AddEquityGroupToWatchlistResponse> ExecuteAsync(
        AddEquityGroupToWatchlistRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            throw new InvalidOperationException("Request is required.");

        if (string.IsNullOrWhiteSpace(request.GroupName))
            throw new InvalidOperationException("GroupName is required.");

        if (string.IsNullOrWhiteSpace(request.DataType))
            throw new InvalidOperationException("DataType is required.");

        if (request.DataType != "lite" && request.DataType != "symbolUpdate")
            throw new InvalidOperationException("DataType must be 'lite' or 'symbolUpdate'.");

        var group = await _equityGroupService.GetGroupByNameAsync(
            request.GroupName,
            cancellationToken);

        if (group is null)
            throw new InvalidOperationException($"Equity group '{request.GroupName}' was not found.");

        var members = await _equityGroupService.GetMembersAsync(
            request.GroupName,
            request.OnlyEnabledMembers,
            cancellationToken);

        if (members.Count == 0)
        {
            return new AddEquityGroupToWatchlistResponse
            {
                GroupName = request.GroupName.Trim().ToUpperInvariant(),
                TotalMemberResolved = 0,
                Upserted = 0,
                Skipped = 0,
                Symbols = new List<string>(),
                Message = "No equity group members found."
            };
        }

        int upserted = 0;
        int skipped = 0;
        var symbols = new List<string>();

        foreach (var member in members)
        {
            if (string.IsNullOrWhiteSpace(member.Symbol))
            {
                skipped++;
                continue;
            }

            var symbol = member.Symbol.Trim().ToUpperInvariant();
            symbols.Add(symbol);

            var watchlistRequest = new UpsertWatchlistItemRequest
            {
                Symbol = symbol,
                DataType = request.DataType
            };

            await _upsertWatchlistItemUseCase.ExecuteAsync(
                watchlistRequest,
                cancellationToken);

            upserted++;
        }

        return new AddEquityGroupToWatchlistResponse
        {
            GroupName = request.GroupName.Trim().ToUpperInvariant(),
            TotalMemberResolved = members.Count,
            Upserted = upserted,
            Skipped = skipped,
            Symbols = symbols,
            Message = "Equity group members added to live watchlist successfully."
        };
    }
}