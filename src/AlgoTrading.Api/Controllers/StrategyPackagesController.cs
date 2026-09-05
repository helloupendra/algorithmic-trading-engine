using System.Text.RegularExpressions;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Contracts.Users;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// Strategy packages: which strategies a trader may run, and the ceilings that
/// come with them.
/// </summary>
/// <remarks>
/// A package that only listed strategies would barely beat a row of checkboxes.
/// It carries limits because on this platform every trader runs on the same
/// broker connection and the same capital, so what a trader may run is also how
/// much they may risk.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/StrategyPackages")]
public class StrategyPackagesController : ControllerBase
{
    private static readonly Regex KeyPattern = new("^[a-z0-9][a-z0-9-]{1,31}$", RegexOptions.Compiled);

    private readonly TradingDbContext _dbContext;
    private readonly StrategyCatalogService _catalog;
    private readonly ILogger<StrategyPackagesController> _logger;

    public StrategyPackagesController(
        TradingDbContext dbContext,
        StrategyCatalogService catalog,
        ILogger<StrategyPackagesController> logger)
    {
        _dbContext = dbContext;
        _catalog = catalog;
        _logger = logger;
    }

    /// <summary>Every strategy the engine can run — the names a package is built from.</summary>
    [HttpGet("catalog")]
    public async Task<IActionResult> GetCatalog(CancellationToken cancellationToken)
    {
        var entries = await _catalog.GetAllAsync(cancellationToken);

        return Ok(entries.Select(x => new
        {
            x.Name,
            x.Category,
            x.Description,
            x.SupportedUnderlyings,
        }));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<StrategyPackageResponse>>> List(
        CancellationToken cancellationToken)
    {
        var packages = await _dbContext.StrategyPackages
            .AsNoTracking()
            .Include(x => x.Items)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        var holders = await _dbContext.AppUsers
            .AsNoTracking()
            .Where(x => x.StrategyPackageId != null)
            .GroupBy(x => x.StrategyPackageId!.Value)
            .Select(g => new { PackageId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PackageId, x => x.Count, cancellationToken);

        return Ok(packages.Select(p => Map(p, holders.GetValueOrDefault(p.Id))).ToList());
    }

    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] SaveStrategyPackageRequest request,
        CancellationToken cancellationToken)
    {
        string key = (request.Key ?? string.Empty).Trim().ToLowerInvariant();

        if (!KeyPattern.IsMatch(key))
        {
            return BadRequest(new
            {
                message = "Key must be 2-32 characters of lowercase letters, digits or dashes.",
            });
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Name is required." });
        }

        if (await _dbContext.StrategyPackages.AnyAsync(x => x.Key == key, cancellationToken))
        {
            return BadRequest(new { message = $"A package with key '{key}' already exists." });
        }

        var now = DateTime.UtcNow;

        var package = new StrategyPackage
        {
            Key = key,
            CreatedBy = User.GetUserName() ?? "admin",
            CreatedUtc = now,
        };

        Apply(package, request);
        package.UpdatedUtc = now;

        _dbContext.StrategyPackages.Add(package);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Strategy package '{Key}' created by {Actor}.", key, package.CreatedBy);

        return Ok(Map(package, 0));
    }

    [HttpPut("{id:long}")]
    public async Task<IActionResult> Update(
        long id,
        [FromBody] SaveStrategyPackageRequest request,
        CancellationToken cancellationToken)
    {
        var package = await _dbContext.StrategyPackages
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (package is null) return NotFound(new { message = $"No package with id {id}." });

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Name is required." });
        }

        Apply(package, request);
        package.UpdatedUtc = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync(cancellationToken);

        int holders = await _dbContext.AppUsers.CountAsync(x => x.StrategyPackageId == id, cancellationToken);

        return Ok(Map(package, holders));
    }

    /// <summary>Replaces the package's membership with exactly these strategies.</summary>
    [HttpPut("{id:long}/strategies")]
    public async Task<IActionResult> SetStrategies(
        long id,
        [FromBody] SetPackageStrategiesRequest request,
        CancellationToken cancellationToken)
    {
        var package = await _dbContext.StrategyPackages
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (package is null) return NotFound(new { message = $"No package with id {id}." });

        var known = (await _catalog.GetAllAsync(cancellationToken))
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var wanted = request.StrategyNames
            .Select(x => x?.Trim() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var name in wanted)
        {
            if (!known.Contains(name))
            {
                // Membership is by name, so a typo would sit there granting
                // nothing and look like it worked.
                return BadRequest(new { message = $"'{name}' is not a strategy the engine can run." });
            }
        }

        foreach (var item in package.Items.Where(i => !wanted.Contains(i.StrategyName, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _dbContext.StrategyPackageItems.Remove(item);
        }

        foreach (var name in wanted.Where(n => !package.Items.Any(i => string.Equals(i.StrategyName, n, StringComparison.OrdinalIgnoreCase))))
        {
            _dbContext.StrategyPackageItems.Add(new StrategyPackageItem
            {
                StrategyPackageId = id,
                StrategyName = name,
            });
        }

        package.UpdatedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var reloaded = await _dbContext.StrategyPackages
            .AsNoTracking()
            .Include(x => x.Items)
            .FirstAsync(x => x.Id == id, cancellationToken);

        int holders = await _dbContext.AppUsers.CountAsync(x => x.StrategyPackageId == id, cancellationToken);

        return Ok(Map(reloaded, holders));
    }

    [HttpDelete("{id:long}")]
    public async Task<IActionResult> Delete(long id, CancellationToken cancellationToken)
    {
        var package = await _dbContext.StrategyPackages.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (package is null) return NotFound(new { message = $"No package with id {id}." });

        int holders = await _dbContext.AppUsers.CountAsync(x => x.StrategyPackageId == id, cancellationToken);

        _dbContext.StrategyPackages.Remove(package);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Strategy package '{Key}' deleted by {Actor}; {Holders} trader(s) left without one.",
            package.Key, User.GetUserName() ?? "admin", holders);

        return Ok(new
        {
            message = holders == 0
                ? $"{package.Name} deleted."
                : $"{package.Name} deleted. {holders} trader(s) now hold no package, so they can run nothing until you assign one.",
        });
    }

    /// <summary>Extra strategies for one trader, on top of their package.</summary>
    [HttpPut("/api/Users/{userId:long}/strategy-grants")]
    public async Task<IActionResult> SetUserStrategyGrants(
        long userId,
        [FromBody] SetStrategyGrantsRequest request,
        CancellationToken cancellationToken)
    {
        var user = await _dbContext.AppUsers
            .Include(x => x.StrategyGrants)
            .FirstOrDefaultAsync(x => x.Id == userId, cancellationToken);

        if (user is null) return NotFound(new { message = $"No account with id {userId}." });

        var known = (await _catalog.GetAllAsync(cancellationToken))
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var wanted = request.StrategyNames
            .Select(x => x?.Trim() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var name in wanted)
        {
            if (!known.Contains(name))
            {
                return BadRequest(new { message = $"'{name}' is not a strategy the engine can run." });
            }
        }

        foreach (var grant in user.StrategyGrants.Where(g => !wanted.Contains(g.StrategyName, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _dbContext.UserStrategyGrants.Remove(grant);
        }

        string actor = User.GetUserName() ?? "admin";

        foreach (var name in wanted.Where(n => !user.StrategyGrants.Any(g => string.Equals(g.StrategyName, n, StringComparison.OrdinalIgnoreCase))))
        {
            _dbContext.UserStrategyGrants.Add(new UserStrategyGrant
            {
                UserId = userId,
                StrategyName = name,
                GrantedBy = actor,
                GrantedUtc = DateTime.UtcNow,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { message = $"{user.UserName} now has {wanted.Count} extra strategy grant(s)." });
    }

    private static void Apply(StrategyPackage package, SaveStrategyPackageRequest request)
    {
        package.Name = request.Name.Trim();
        package.Description = (request.Description ?? string.Empty).Trim();
        package.IsEnabled = request.IsEnabled;
        package.IncludesAllStrategies = request.IncludesAllStrategies;
        package.MaxLotsPerRun = request.MaxLotsPerRun is > 0 ? request.MaxLotsPerRun : null;
        package.MaxConcurrentRuns = request.MaxConcurrentRuns is > 0 ? request.MaxConcurrentRuns : null;
        package.AllowLiveMode = request.AllowLiveMode;
        package.AllowedUnderlyingsCsv = string.Join(
            ",",
            request.AllowedUnderlyings
                .Select(x => x?.Trim().ToUpperInvariant() ?? string.Empty)
                .Where(x => x.Length > 0)
                .Distinct());
    }

    private static StrategyPackageResponse Map(StrategyPackage package, int holders) => new()
    {
        Id = package.Id,
        Key = package.Key,
        Name = package.Name,
        Description = package.Description,
        IsEnabled = package.IsEnabled,
        IncludesAllStrategies = package.IncludesAllStrategies,
        MaxLotsPerRun = package.MaxLotsPerRun,
        MaxConcurrentRuns = package.MaxConcurrentRuns,
        AllowLiveMode = package.AllowLiveMode,
        AllowedUnderlyings = package.AllowedUnderlyingsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList(),
        Strategies = package.Items.Select(x => x.StrategyName).OrderBy(x => x).ToList(),
        HolderCount = holders,
        CreatedBy = package.CreatedBy,
        UpdatedUtc = package.UpdatedUtc,
    };
}
