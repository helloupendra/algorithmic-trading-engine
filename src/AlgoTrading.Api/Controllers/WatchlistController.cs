using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services.LiveFeed;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// A trader's own watchlist — the symbols they want to look at.
/// </summary>
/// <remarks>
/// This is deliberately <em>not</em> the ingestor's subscription list. That list
/// is what the live feed connects to and what every running strategy depends on;
/// a trader removing a symbol from their own view must never unsubscribe the
/// feed and starve someone else's live run.
/// <para>
/// Adding here therefore also ensures the ingestor subscribes — otherwise no
/// quotes would arrive for the new symbol — while removing here removes only
/// this trader's row.
/// </para>
/// </remarks>
[Authorize]
[RequireModule(PlatformModules.MarketData)]
[ApiController]
[Route("api/Watchlist")]
public class WatchlistController : ControllerBase
{
    /// <summary>
    /// What a new trader starts with: nothing. The three indices used to be
    /// seeded here so the page was not an empty box, but the market pulse
    /// on the overview now shows them to everyone; the list is purely the
    /// trader's own picks, so a symbol on it is one they put there.
    /// </summary>
    private static readonly string[] DefaultSymbols = System.Array.Empty<string>();

    private readonly TradingDbContext _dbContext;
    private readonly ILiveDataService _liveData;
    private readonly ILogger<WatchlistController> _logger;

    public WatchlistController(
        TradingDbContext dbContext,
        ILiveDataService liveData,
        ILogger<WatchlistController> logger)
    {
        _dbContext = dbContext;
        _liveData = liveData;
        _logger = logger;
    }

    /// <summary>This trader's symbols, with the last saved quote for each.</summary>
    [HttpGet("me")]
    public async Task<IActionResult> GetMine(CancellationToken cancellationToken)
    {
        long userId = User.GetRequiredUserId();

        var items = await LoadOrSeedAsync(userId, cancellationToken);

        var symbols = items.Select(x => x.Symbol).ToList();

        var quotes = await _dbContext.LiveQuotesLatest
            .AsNoTracking()
            .Where(x => symbols.Contains(x.Symbol))
            .ToDictionaryAsync(x => x.Symbol, cancellationToken);

        // Whether the feed is subscribed decides if a quote will ever refresh, so
        // say it per row rather than leaving a symbol silently frozen.
        var subscribed = await _dbContext.LiveWatchlistItems
            .AsNoTracking()
            .Where(x => x.IsActive && symbols.Contains(x.Symbol))
            .Select(x => x.Symbol)
            .ToListAsync(cancellationToken);

        return Ok(items.Select(item =>
        {
            quotes.TryGetValue(item.Symbol, out var quote);

            return new
            {
                item.Symbol,
                item.SortOrder,
                IsSubscribed = subscribed.Contains(item.Symbol),
                LastTradedPrice = quote?.LastTradedPrice,
                quote?.Open,
                quote?.High,
                quote?.Low,
                quote?.Close,
                quote?.Volume,
                UpdatedUtc = quote?.UpdatedUtc,
            };
        }));
    }

    public record AddSymbolRequest(string Symbol);

    /// <summary>The most symbols one trader's own list holds.</summary>
    public const int MaxOwnSymbols = 50;

    /// <summary>Adds a symbol, and makes sure the feed will actually carry it.</summary>
    [HttpPost("me")]
    public async Task<IActionResult> AddMine(
        [FromBody] AddSymbolRequest request,
        CancellationToken cancellationToken)
    {
        string symbol = (request.Symbol ?? string.Empty).Trim().ToUpperInvariant();

        if (string.IsNullOrWhiteSpace(symbol))
        {
            return BadRequest(new { message = "Symbol is required." });
        }

        long userId = User.GetRequiredUserId();

        if (await _dbContext.UserWatchlistItems.AnyAsync(
                x => x.UserId == userId && x.Symbol == symbol, cancellationToken))
        {
            return Ok(new { message = $"{symbol} is already on your watchlist." });
        }

        // Only real, live instruments (24 Nov 2026 review: any string went onto
        // the recording list), or what the feed already carries, such as the
        // indices.
        var todayIst = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
        bool known = await _dbContext.LiveWatchlistItems.AnyAsync(x => x.Symbol == symbol && x.IsActive, cancellationToken)
            || await _dbContext.Instruments.AnyAsync(
                x => x.Symbol == symbol && x.IsEnabled && (x.ExpiryDate == null || x.ExpiryDate >= todayIst), cancellationToken);
        if (!known)
        {
            return BadRequest(new { message = $"{symbol} is not a live instrument. Pick it from the search." });
        }

        int held = await _dbContext.UserWatchlistItems.CountAsync(x => x.UserId == userId, cancellationToken);
        if (held >= MaxOwnSymbols)
        {
            return BadRequest(new { message = $"Your watchlist holds {MaxOwnSymbols} symbols, the most it can. Remove one first." });
        }

        int nextOrder = await _dbContext.UserWatchlistItems
            .Where(x => x.UserId == userId)
            .Select(x => (int?)x.SortOrder)
            .MaxAsync(cancellationToken) ?? 0;

        _dbContext.UserWatchlistItems.Add(new UserWatchlistItem
        {
            UserId = userId,
            Symbol = symbol,
            SortOrder = nextOrder + 10,
            CreatedUtc = DateTime.UtcNow,
        });

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Without a subscription the row would sit there showing nothing forever.
        // A trader's symbol goes on at the lowest priority, within a daily
        // allowance, and one already carried is left as it is.
        var outcome = await PersonFeedRequests.RequestAsync(_dbContext, User, symbol,
            priority => _liveData.UpsertWatchlistItemAsync(
                new UpsertWatchlistItemRequest { Symbol = symbol, IsActive = true, Priority = priority },
                cancellationToken),
            cancellationToken);

        return Ok(new
        {
            message = outcome == FeedRequestOutcome.OverDailyLimit
                ? $"{symbol} added to your list. The live feed takes {PersonFeedRequests.PerTraderPerDay} new symbols a day from you, so it shows no price until tomorrow."
                : $"{symbol} added. The feed will pick it up on its next refresh.",
        });
    }

    /// <summary>
    /// Removes a symbol from this trader's list only. The feed keeps carrying it —
    /// another trader or a running strategy may still need it.
    /// </summary>
    [HttpDelete("me/{symbol}")]
    public async Task<IActionResult> RemoveMine(string symbol, CancellationToken cancellationToken)
    {
        long userId = User.GetRequiredUserId();
        string normalized = (symbol ?? string.Empty).Trim().ToUpperInvariant();

        var row = await _dbContext.UserWatchlistItems
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Symbol == normalized, cancellationToken);

        if (row is null)
        {
            return NotFound(new { message = $"{normalized} is not on your watchlist." });
        }

        _dbContext.UserWatchlistItems.Remove(row);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            message = $"{normalized} removed from your watchlist. The live feed still carries it for everyone else.",
        });
    }

    /// <summary>Puts the three default index symbols back.</summary>
    [HttpPost("me/reset")]
    public async Task<IActionResult> ResetMine(CancellationToken cancellationToken)
    {
        long userId = User.GetRequiredUserId();

        var existing = await _dbContext.UserWatchlistItems
            .Where(x => x.UserId == userId)
            .ToListAsync(cancellationToken);

        _dbContext.UserWatchlistItems.RemoveRange(existing);
        await _dbContext.SaveChangesAsync(cancellationToken);

        await LoadOrSeedAsync(userId, cancellationToken);

        return Ok(new { message = "Watchlist cleared." });
    }

    /// <summary>
    /// Reads this trader's list, seeding the defaults the first time they look.
    /// </summary>
    private async Task<List<UserWatchlistItem>> LoadOrSeedAsync(
        long userId,
        CancellationToken cancellationToken)
    {
        var items = await _dbContext.UserWatchlistItems
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Symbol)
            .ToListAsync(cancellationToken);

        if (items.Count > 0 || DefaultSymbols.Length == 0) return items;

        var now = DateTime.UtcNow;
        int order = 0;

        foreach (var symbol in DefaultSymbols)
        {
            _dbContext.UserWatchlistItems.Add(new UserWatchlistItem
            {
                UserId = userId,
                Symbol = symbol,
                SortOrder = order,
                CreatedUtc = now,
            });
            order += 10;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Seeded the default watchlist for user {UserId}.", userId);

        return await _dbContext.UserWatchlistItems
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);
    }
}
