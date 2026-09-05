using AlgoTrading.Api.Security;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The activity log: who did what, across every module.
/// </summary>
/// <remarks>
/// Admin-only. It records actions by everyone — admins included — so it must not
/// be readable by the people it watches.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/ActivityLog")]
public class ActivityLogController : ControllerBase
{
    private const int MaxPageSize = 500;

    private readonly TradingDbContext _dbContext;

    public ActivityLogController(TradingDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// Entries, newest first, narrowed by any combination of the filters.
    /// </summary>
    /// <param name="userId">One account's trail.</param>
    /// <param name="module">"strategies", "data", "users"…</param>
    /// <param name="action">Exact action, e.g. "create:deploy".</param>
    /// <param name="succeeded">False shows only refusals and failures.</param>
    /// <param name="search">Matches the path, the summary or the username.</param>
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] long? userId,
        [FromQuery] string? module,
        [FromQuery] string? action,
        [FromQuery] bool? succeeded,
        [FromQuery] string? search,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int limit = 200,
        [FromQuery] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var query = Filtered(userId, module, action, succeeded, search, fromUtc, toUtc);

        int total = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderByDescending(x => x.OccurredUtc)
            .ThenByDescending(x => x.Id)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, MaxPageSize))
            .Select(x => new
            {
                x.Id,
                x.OccurredUtc,
                x.UserId,
                x.UserName,
                x.Role,
                x.Module,
                x.Action,
                x.Method,
                x.Path,
                x.StatusCode,
                x.DurationMs,
                x.Succeeded,
                x.TargetType,
                x.TargetId,
                x.Summary,
                x.IpAddress,
            })
            .ToListAsync(cancellationToken);

        return Ok(new { total, rows });
    }

    /// <summary>
    /// The filter options that actually exist in the data, so the console never
    /// offers a choice that returns nothing.
    /// </summary>
    [HttpGet("facets")]
    public async Task<IActionResult> GetFacets(CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddDays(-90);

        var modules = await _dbContext.ActivityLog
            .AsNoTracking()
            .Where(x => x.OccurredUtc >= since)
            .GroupBy(x => x.Module)
            .Select(g => new { Module = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToListAsync(cancellationToken);

        var actions = await _dbContext.ActivityLog
            .AsNoTracking()
            .Where(x => x.OccurredUtc >= since)
            .GroupBy(x => x.Action)
            .Select(g => new { Action = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(60)
            .ToListAsync(cancellationToken);

        var users = await _dbContext.ActivityLog
            .AsNoTracking()
            .Where(x => x.OccurredUtc >= since)
            .GroupBy(x => new { x.UserId, x.UserName })
            .Select(g => new
            {
                g.Key.UserId,
                g.Key.UserName,
                Count = g.Count(),
                Failures = g.Count(x => !x.Succeeded),
                LastUtc = g.Max(x => x.OccurredUtc),
            })
            .OrderByDescending(x => x.LastUtc)
            .ToListAsync(cancellationToken);

        return Ok(new { modules, actions, users });
    }

    /// <summary>
    /// One account's activity, rolled up — what the console shows when an admin
    /// clicks a person.
    /// </summary>
    [HttpGet("users/{userId:long}/summary")]
    public async Task<IActionResult> GetUserSummary(long userId, CancellationToken cancellationToken)
    {
        var entries = _dbContext.ActivityLog.AsNoTracking().Where(x => x.UserId == userId);

        int total = await entries.CountAsync(cancellationToken);

        if (total == 0)
        {
            return Ok(new { total = 0, failures = 0, firstUtc = (DateTime?)null, lastUtc = (DateTime?)null, byModule = Array.Empty<object>() });
        }

        var byModule = await entries
            .GroupBy(x => x.Module)
            .Select(g => new
            {
                Module = g.Key,
                Count = g.Count(),
                Failures = g.Count(x => !x.Succeeded),
                LastUtc = g.Max(x => x.OccurredUtc),
            })
            .OrderByDescending(x => x.Count)
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            total,
            failures = await entries.CountAsync(x => !x.Succeeded, cancellationToken),
            firstUtc = await entries.MinAsync(x => x.OccurredUtc, cancellationToken),
            lastUtc = await entries.MaxAsync(x => x.OccurredUtc, cancellationToken),
            byModule,
        });
    }

    private IQueryable<ActivityLogEntry> Filtered(
        long? userId,
        string? module,
        string? action,
        bool? succeeded,
        string? search,
        DateTime? fromUtc,
        DateTime? toUtc)
    {
        var query = _dbContext.ActivityLog.AsNoTracking();

        if (userId is not null) query = query.Where(x => x.UserId == userId);
        if (!string.IsNullOrWhiteSpace(module)) query = query.Where(x => x.Module == module);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action == action);
        if (succeeded is not null) query = query.Where(x => x.Succeeded == succeeded);
        if (fromUtc is not null) query = query.Where(x => x.OccurredUtc >= fromUtc);
        if (toUtc is not null) query = query.Where(x => x.OccurredUtc <= toUtc);

        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim();
            query = query.Where(x =>
                EF.Functions.ILike(x.Path, $"%{term}%") ||
                EF.Functions.ILike(x.UserName, $"%{term}%") ||
                (x.Summary != null && EF.Functions.ILike(x.Summary, $"%{term}%")));
        }

        return query;
    }
}
