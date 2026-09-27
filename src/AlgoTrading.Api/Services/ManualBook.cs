// src/AlgoTrading.Api/Services/ManualBook.cs
using AlgoTrading.Api.Controllers;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Finding and opening a user's manual book: the long-lived run, one per user,
/// that holds what they trade by hand.
/// </summary>
/// <remarks>
/// Two callers since 27 Sep: the order ticket, and the market close, which
/// moves a strategy run's carry-forward legs into the owner's book — creating
/// the book for an owner who has never placed a manual order. Both open it
/// the same way, so there is one place that says what a book is.
/// </remarks>
public static class ManualBook
{
    // Two writers can want a book for the same person in the same second (a
    // ticket order at 15:30:00 and the close's carry). Without the gate both
    // would find none and each open one, and the console, which shows the
    // newest, would lose sight of the other's positions.
    private static readonly SemaphoreSlim CreateGate = new(1, 1);

    /// <summary>The user's open manual book, or null when they have none.</summary>
    public static Task<SimulationRun?> FindAsync(TradingDbContext db, long userId, CancellationToken cancellationToken) =>
        db.SimulationRuns
            .Where(x => x.UserId == userId
                        && x.StrategyName == ManualOrdersController.BookStrategyName
                        && x.Status == "Running")
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The user's open manual book, opened now if they have none.</summary>
    public static async Task<SimulationRun> FindOrCreateAsync(
        TradingDbContext db,
        long userId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var existing = await FindAsync(db, userId, cancellationToken);
        if (existing is not null) return existing;

        await CreateGate.WaitAsync(cancellationToken);
        try
        {
            existing = await FindAsync(db, userId, cancellationToken);
            if (existing is not null) return existing;

            var book = NewBook(userId, DateTime.UtcNow);
            db.SimulationRuns.Add(book);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Opened manual book run {RunId} for user {UserId}.", book.Id, userId);
            return book;
        }
        finally
        {
            CreateGate.Release();
        }
    }

    /// <summary>
    /// A book as it is opened. One per user and long-lived: manual positions are
    /// held across days, so a book per session would scatter one running
    /// position history over many runs.
    /// </summary>
    public static SimulationRun NewBook(long userId, DateTime nowUtc) => new()
    {
        UserId = userId,
        Mode = StrategyRunControl.LivePaperMode,
        // Not one instrument: the book holds whatever is traded into it.
        Symbol = "MANUAL",
        Resolution = "1m",
        ReplaySpeed = string.Empty,
        Status = StrategyRunControl.RunStatusRunning,
        StrategyName = ManualOrdersController.BookStrategyName,
        ParametersJson = "{}",
        LastError = string.Empty,
        InitialCapital = 1_000_000m,
        CreatedUtc = nowUtc,
        StartedUtc = nowUtc
    };
}
