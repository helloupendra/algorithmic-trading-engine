using System.Text;
using System.Text.Json;
using AlgoTrading.Api.Security;
using AlgoTrading.Contracts.Notebook;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// A trader's whiteboards — the Notebook module's infinite canvases.
/// </summary>
/// <remarks>
/// The server stores the scene as an opaque JSON object and never interprets it;
/// the canvas editor owns that format. What the server does own is the race: a
/// board sits open in a tab for hours, and the same trader in a second tab (or an
/// admin having a look) must not overwrite it with a stale copy. Every save
/// carries the version it started from and is applied as a compare-and-set, so
/// the loser gets a 409 with what is on the server now instead of a silent loss.
/// <para>
/// Boards are private to their owner. Admins can list, open and edit every board
/// — the same rule as runs — and the list tells them whose each one is.
/// </para>
/// </remarks>
[Authorize]
[RequireModule(PlatformModules.Notebook)]
[ApiController]
[Route("api/Whiteboards")]
public class WhiteboardsController : ControllerBase
{
    private const string DefaultName = "Untitled board";
    private const int MaxNameLength = 120;

    /// <summary>
    /// Enough for a dense board with a few embedded screenshots; anything bigger
    /// is almost certainly a pasted image that belongs somewhere else.
    /// </summary>
    private const int MaxSceneBytes = 5 * 1024 * 1024;

    private readonly TradingDbContext _dbContext;

    public WhiteboardsController(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>The caller's boards, most recently touched first; every board for an admin.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WhiteboardSummary>>> List(CancellationToken cancellationToken)
    {
        IQueryable<Whiteboard> boards = _dbContext.Whiteboards.AsNoTracking();

        if (!User.IsAdmin())
        {
            long userId = User.GetRequiredUserId();
            boards = boards.Where(x => x.OwnerUserId == userId);
        }

        // Projected, not loaded: a scene can be megabytes and the list never shows it.
        var rows = await boards
            .LeftJoin(
                _dbContext.AppUsers,
                board => board.OwnerUserId,
                user => user.Id,
                (board, user) => new WhiteboardSummary
                {
                    Id = board.Id,
                    Name = board.Name,
                    OwnerUserId = board.OwnerUserId,
                    OwnerUserName = user != null ? user.UserName : null,
                    Version = board.Version,
                    CreatedUtc = board.CreatedUtc,
                    UpdatedUtc = board.UpdatedUtc,
                    UpdatedBy = board.UpdatedBy,
                })
            .OrderByDescending(x => x.UpdatedUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);

        return Ok(rows);
    }

    /// <summary>Creates an empty board owned by the caller.</summary>
    [HttpPost]
    public async Task<ActionResult<WhiteboardDetail>> Create(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateWhiteboardRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeName(request?.Name, out string name, out string? error))
            return BadRequest(new { message = error });

        var now = DateTime.UtcNow;
        var board = new Whiteboard
        {
            Name = name,
            OwnerUserId = User.GetRequiredUserId(),
            CreatedUtc = now,
            UpdatedUtc = now,
            UpdatedBy = User.GetUserName(),
        };

        _dbContext.Whiteboards.Add(board);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(Get), new { id = board.Id }, ToDetail(board, User.GetUserName()));
    }

    /// <summary>One board with its scene. 404 when unknown; 403 when it is someone else's.</summary>
    [HttpGet("{id:long}")]
    public async Task<ActionResult<WhiteboardDetail>> Get(long id, CancellationToken cancellationToken)
    {
        var row = await _dbContext.Whiteboards
            .AsNoTracking()
            .Where(x => x.Id == id)
            .LeftJoin(
                _dbContext.AppUsers,
                board => board.OwnerUserId,
                user => user.Id,
                (board, user) => new { Board = board, OwnerUserName = user != null ? user.UserName : null })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
            return NotFound(new { message = $"Whiteboard {id} does not exist." });

        if (!CanAccess(row.Board.OwnerUserId))
            return Forbid();

        return Ok(ToDetail(row.Board, row.OwnerUserName));
    }

    /// <summary>
    /// Replaces the scene, but only if nobody has saved since the caller loaded it.
    /// </summary>
    /// <remarks>
    /// The check and the write are one UPDATE ... WHERE Version = expected, so two
    /// tabs saving in the same instant cannot both win: the database serialises
    /// them and the second sees zero rows. A 409 then reports the current version
    /// and who saved it, which is what the client needs to offer a reload.
    /// </remarks>
    [HttpPut("{id:long}/scene")]
    public async Task<ActionResult<SaveSceneResponse>> SaveScene(
        long id,
        [FromBody] SaveSceneRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateScene(request.SceneJson, out string? error))
            return BadRequest(new { message = error });

        string sceneJson = request.SceneJson!;

        var current = await _dbContext.Whiteboards
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.OwnerUserId })
            .FirstOrDefaultAsync(cancellationToken);

        if (current is null)
            return NotFound(new { message = $"Whiteboard {id} does not exist." });

        if (!CanAccess(current.OwnerUserId))
            return Forbid();

        var now = DateTime.UtcNow;
        string? savedBy = User.GetUserName();

        int written = await _dbContext.Whiteboards
            .Where(x => x.Id == id && x.Version == request.Version)
            .ExecuteUpdateAsync(set => set
                .SetProperty(x => x.SceneJson, sceneJson)
                .SetProperty(x => x.Version, x => x.Version + 1)
                .SetProperty(x => x.UpdatedUtc, now)
                .SetProperty(x => x.UpdatedBy, savedBy),
                cancellationToken);

        if (written == 1)
            return Ok(new SaveSceneResponse { Version = request.Version + 1, UpdatedUtc = now });

        // Zero rows: either the version moved on, or the board went away between
        // the ownership check and the write. Tell them apart for the caller.
        var latest = await _dbContext.Whiteboards
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.Version, x.UpdatedUtc, x.UpdatedBy })
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is null)
            return NotFound(new { message = $"Whiteboard {id} was deleted." });

        return Conflict(new SceneConflictResponse
        {
            Message = string.IsNullOrWhiteSpace(latest.UpdatedBy)
                ? $"This board was saved elsewhere (version {latest.Version}); you loaded version {request.Version}. Reload to pick up the latest scene."
                : $"{latest.UpdatedBy} saved this board first (version {latest.Version}); you loaded version {request.Version}. Reload to pick up the latest scene.",
            Version = latest.Version,
            UpdatedUtc = latest.UpdatedUtc,
            UpdatedBy = latest.UpdatedBy,
        });
    }

    /// <summary>Renames a board. The version is untouched — it tracks the scene, not the label.</summary>
    [HttpPatch("{id:long}")]
    public async Task<ActionResult<WhiteboardSummary>> Rename(
        long id,
        [FromBody] RenameWhiteboardRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeName(request.Name, out string name, out string? error))
            return BadRequest(new { message = error });

        var board = await _dbContext.Whiteboards.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (board is null)
            return NotFound(new { message = $"Whiteboard {id} does not exist." });

        if (!CanAccess(board.OwnerUserId))
            return Forbid();

        board.Name = name;
        board.UpdatedUtc = DateTime.UtcNow;
        board.UpdatedBy = User.GetUserName();
        await _dbContext.SaveChangesAsync(cancellationToken);

        string? ownerUserName = await _dbContext.AppUsers
            .AsNoTracking()
            .Where(x => x.Id == board.OwnerUserId)
            .Select(x => x.UserName)
            .FirstOrDefaultAsync(cancellationToken);

        return Ok(ToSummary(board, ownerUserName));
    }

    /// <summary>Deletes a board outright; there is no bin.</summary>
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var current = await _dbContext.Whiteboards
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new { x.OwnerUserId })
            .FirstOrDefaultAsync(cancellationToken);

        if (current is null)
            return NotFound(new { message = $"Whiteboard {id} does not exist." });

        if (!CanAccess(current.OwnerUserId))
            return Forbid();

        await _dbContext.Whiteboards
            .Where(x => x.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        return NoContent();
    }

    /// <summary>Admins reach any board; a trader only their own.</summary>
    private bool CanAccess(long ownerUserId)
        => User.IsAdmin() || User.GetUserId() == ownerUserId;

    private static bool TryNormalizeName(string? raw, out string name, out string? error)
    {
        name = (raw ?? string.Empty).Trim();
        error = null;

        if (name.Length == 0)
            name = DefaultName;

        if (name.Length > MaxNameLength)
        {
            error = $"Name must be at most {MaxNameLength} characters.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The one thing the server asserts about a scene: it is a single JSON object
    /// of a sane size. The editor's own schema is its business.
    /// </summary>
    private static bool TryValidateScene(string? sceneJson, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(sceneJson))
        {
            error = "sceneJson is required.";
            return false;
        }

        // Bytes, not characters: the column and the wire both count UTF-8.
        if (Encoding.UTF8.GetByteCount(sceneJson) > MaxSceneBytes)
        {
            error = $"Scene is larger than {MaxSceneBytes / (1024 * 1024)} MB. Remove embedded images and try again.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(sceneJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "sceneJson must be a JSON object.";
                return false;
            }
        }
        catch (JsonException)
        {
            error = "sceneJson is not valid JSON.";
            return false;
        }

        return true;
    }

    private static WhiteboardSummary ToSummary(Whiteboard board, string? ownerUserName)
        => Fill(new WhiteboardSummary(), board, ownerUserName);

    private static WhiteboardDetail ToDetail(Whiteboard board, string? ownerUserName)
    {
        var detail = new WhiteboardDetail { SceneJson = board.SceneJson };
        Fill(detail, board, ownerUserName);
        return detail;
    }

    private static T Fill<T>(T dto, Whiteboard board, string? ownerUserName) where T : WhiteboardSummary
    {
        dto.Id = board.Id;
        dto.Name = board.Name;
        dto.OwnerUserId = board.OwnerUserId;
        dto.OwnerUserName = ownerUserName;
        dto.Version = board.Version;
        dto.CreatedUtc = board.CreatedUtc;
        dto.UpdatedUtc = board.UpdatedUtc;
        dto.UpdatedBy = board.UpdatedBy;
        return dto;
    }
}
