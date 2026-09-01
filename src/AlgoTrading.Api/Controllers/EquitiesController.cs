// src/AlgoTrading.Api/Controllers/EquitiesController.cs
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.Equities;
using AlgoTrading.Contracts.Equities;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace AlgoTrading.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EquitiesController : ControllerBase
{
    private readonly IEquityGroupService _equityGroupService;
    private readonly IEquityLiveSnapshotService _equityLiveSnapshotService;
    private readonly AddEquityGroupToWatchlistUseCase _addEquityGroupToWatchlistUseCase;

    public EquitiesController(
        IEquityGroupService equityGroupService,
        IEquityLiveSnapshotService equityLiveSnapshotService,
        AddEquityGroupToWatchlistUseCase addEquityGroupToWatchlistUseCase)
    {
        _equityGroupService = equityGroupService;
        _equityLiveSnapshotService = equityLiveSnapshotService;
        _addEquityGroupToWatchlistUseCase = addEquityGroupToWatchlistUseCase;
    }

    [HttpPost("live/watchlist/group")]
    public async Task<IActionResult> AddGroupToWatchlist(
        [FromBody] AddEquityGroupToWatchlistRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            return BadRequest(new { message = "Request body is required." });
        
        if (string.IsNullOrWhiteSpace(request.GroupName))
            return BadRequest(new { message = "GroupName is required." });
        
        if (string.IsNullOrWhiteSpace(request.DataType))
            return BadRequest(new { message = "DataType is required." });

        if (request.DataType != "lite" && request.DataType != "symbolUpdate")
            return BadRequest(new { message = "DataType must be 'lite' or 'symbolUpdate'." });

        try
        {
            var result = await _addEquityGroupToWatchlistUseCase.ExecuteAsync(
                request,
                cancellationToken);

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("groups")]
    public async Task<IActionResult> CreateGroup(
        [FromBody] CreateEquityGroupRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
            return BadRequest(new { message = "Request body is required." });

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var result = await _equityGroupService.CreateGroupAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetGroupByName), new { name = result.Name }, result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("groups/{name}/members")]
    public async Task<IActionResult> AddGroupMember(
        [FromRoute] string name,
        [FromBody] AddEquityGroupMemberRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "Group name is required." });

        if (request is null)
            return BadRequest(new { message = "Request body is required." });

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var result = await _equityGroupService.AddMemberAsync(name, request, cancellationToken);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("groups")]
    public async Task<IActionResult> GetGroups(
        [FromQuery] bool onlyEnabled = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _equityGroupService.GetGroupsAsync(
            onlyEnabled,
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("groups/{name}")]
    public async Task<IActionResult> GetGroupByName(
        [FromRoute] string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "Group name is required." });

        try
        {
            var result = await _equityGroupService.GetGroupByNameAsync(
                name,
                cancellationToken);

            if (result is null)
                return NotFound(new { message = "Equity group not found." });

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("groups/{name}/members")]
    public async Task<IActionResult> GetGroupMembers(
        [FromRoute] string name,
        [FromQuery] bool onlyEnabled = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "Group name is required." });

        try
        {
            var group = await _equityGroupService.GetGroupByNameAsync(
                name,
                cancellationToken);

            if (group is null)
                return NotFound(new { message = "Equity group not found." });

            var members = await _equityGroupService.GetMembersAsync(
                name,
                onlyEnabled,
                cancellationToken);

            return Ok(members);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("live/latest/group")]
    public async Task<IActionResult> GetLatestByGroup(
    [FromQuery] string groupName,
    [FromQuery] bool onlyEnabled = true,
    CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return BadRequest(new { message = "groupName is required." });

        try
        {
            var result = await _equityLiveSnapshotService.GetLatestByGroupAsync(
                groupName,
                onlyEnabled,
                cancellationToken);

            if (result is null)
                return NotFound(new { message = "Equity group not found." });

            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}
