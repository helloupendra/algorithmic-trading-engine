using System.Text.Json;
using AlgoTrading.Contracts.Portfolio;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Portfolio;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.Portfolio;

/// <summary>A sync's answer, or how long the user must wait for the next one.</summary>
public sealed record PortfolioSyncOutcome(PortfolioSyncResponse? Response, TimeSpan RetryAfter);

/// <summary>
/// Reads every broker a user linked, now: one snapshot per broker that answered, the link's state updated either way,
/// and the report's summary written for the new snapshots.
/// </summary>
/// <remarks>
/// <para>
/// A broker that refuses the token marks its link expired (the user connects again); one that fails otherwise marks
/// it error and keeps the token, since the next try may work. Neither stops the other brokers, and neither deletes
/// the snapshots already taken: the report keeps showing the last good read and says how old it is.
/// </para>
/// <para>
/// Once a minute per user (<see cref="PortfolioSyncGate"/>). Old snapshots (<see cref="PortfolioSettings.RetentionDays"/>)
/// are deleted at each sync, and daily for everyone by <see cref="PortfolioRetentionService"/>.
/// </para>
/// </remarks>
public sealed class PortfolioSyncService(
    TradingDbContext db,
    IEnumerable<IPortfolioBroker> brokers,
    PortfolioTokenVault vault,
    PortfolioPrices prices,
    PortfolioReportService reports,
    PortfolioSummaryWriter summaries,
    PortfolioSyncGate gate,
    IOptionsMonitor<PortfolioSettings> settings,
    ILogger<PortfolioSyncService> logger,
    TimeProvider? time = null)
{
    /// <summary>The longest a sync waits for the written summary; the figures are saved before it is asked.</summary>
    public static readonly TimeSpan SummaryTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// What a sync may spend after the brokers have answered: the desk's prices, the save and the summary. It runs on
    /// its own clock, not the request's, so a browser that gives up (or a proxy that times out) does not throw away
    /// the brokers' answers after the minute's sync has been spent on them.
    /// </summary>
    public static readonly TimeSpan AfterReadsBudget = TimeSpan.FromSeconds(120);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<PortfolioSyncOutcome> SyncAsync(long userId, CancellationToken cancellationToken)
    {
        var s = settings.CurrentValue;
        if (!gate.TryEnter(userId, TimeSpan.FromSeconds(Math.Max(1, s.SyncIntervalSeconds)), out var wait))
        {
            return new PortfolioSyncOutcome(null, wait);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var links = await db.PortfolioConnections.Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        var results = new List<PortfolioSyncResult>();
        var read = new Dictionary<string, BrokerPortfolio>(StringComparer.Ordinal);

        foreach (var link in links.OrderBy(l => PortfolioBrokers.All.ToList().IndexOf(l.Broker)))
        {
            string name = PortfolioBrokers.NameOf(link.Broker);
            link.LastSyncUtc = now;
            var client = brokers.FirstOrDefault(b => b.Key == link.Broker);
            var token = vault.Unprotect(link.TokenProtected);
            string? error = null;

            if (client is null)
            {
                error = $"{name} cannot be read on this server.";
                link.Status = PortfolioConnectionStatus.Error;
            }
            else if (token is null)
            {
                error = $"The saved {name} sign-in could not be read. Connect {name} again.";
                link.Status = PortfolioConnectionStatus.Expired;
            }
            else if (link.ExpiresUtc is DateTime end && end <= now)
            {
                error = $"The {name} sign-in ended at {IstTime.ToIst(end):dd MMM HH:mm} IST. Connect {name} again.";
                link.Status = PortfolioConnectionStatus.Expired;
            }
            else
            {
                try
                {
                    read[link.Broker] = await client.ReadAsync(token, cancellationToken);
                    link.Status = PortfolioConnectionStatus.Connected;
                }
                catch (PortfolioBrokerException ex)
                {
                    error = ex.Message;
                    link.Status = ex.TokenRejected ? PortfolioConnectionStatus.Expired : PortfolioConnectionStatus.Error;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Not the broker's own words: an exception's text can carry a request, and a request carries the token.
                    logger.LogWarning("Portfolio sync of {Broker} for user {UserId} failed: {Error}", link.Broker, userId, ex.GetType().Name);
                    error = $"{name} could not be read ({ex.GetType().Name}). Try again later.";
                    link.Status = PortfolioConnectionStatus.Error;
                }
            }

            link.LastError = error is null ? null : (error.Length <= 500 ? error : error[..500]);
            results.Add(new PortfolioSyncResult(link.Broker, error is null, error));
        }

        using var afterReads = new CancellationTokenSource(AfterReadsBudget);
        var work = afterReads.Token;

        if (read.Count > 0)
        {
            var holdings = read.ToDictionary(r => r.Key, r => r.Value.Holdings);
            try
            {
                holdings = new Dictionary<string, IReadOnlyList<BrokerHolding>>(await prices.EnrichAsync(holdings, now, work));
            }
            catch (Exception ex)
            {
                // The broker's figures are worth keeping without the desk's (or after the budget ran out on the desk's
                // history connector): the report says what is not known.
                logger.LogWarning(ex, "Portfolio prices for user {UserId} could not be added; the snapshots keep the brokers' own figures", userId);
            }

            foreach (var (broker, portfolio) in read)
            {
                db.PortfolioSnapshots.Add(new PortfolioSnapshot
                {
                    UserId = userId,
                    Broker = broker,
                    TakenUtc = now,
                    HoldingsJson = JsonSerializer.Serialize(holdings[broker], PortfolioReportService.SnapshotJson),
                    PositionsJson = JsonSerializer.Serialize(portfolio.Positions, PortfolioReportService.SnapshotJson),
                    FundsJson = JsonSerializer.Serialize(portfolio.Funds, PortfolioReportService.SnapshotJson),
                });
            }
        }

        var cutoff = now.AddDays(-Math.Max(1, s.RetentionDays));
        db.PortfolioSnapshots.RemoveRange(await db.PortfolioSnapshots.Where(x => x.UserId == userId && x.TakenUtc < cutoff).ToListAsync(CancellationToken.None));
        await db.SaveChangesAsync(CancellationToken.None);

        if (read.Count > 0) await WriteSummaryAsync(userId, work);

        logger.LogInformation("Portfolio sync for user {UserId}: {Read} of {Linked} brokers read", userId, read.Count, links.Count);
        return new PortfolioSyncOutcome(new PortfolioSyncResponse(now, results), TimeSpan.Zero);
    }

    /// <summary>The summary for the report the new snapshots make, cached on the newest of them. Never fails the sync.</summary>
    private async Task WriteSummaryAsync(long userId, CancellationToken cancellationToken)
    {
        try
        {
            var built = await reports.BuildAsync(userId, cancellationToken);
            if (built is null) return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SummaryTimeout);
            PortfolioSummaryOutcome outcome;
            try
            {
                outcome = await summaries.WriteAsync(userId, built.Report, timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                outcome = new PortfolioSummaryOutcome(null, PortfolioSummaryWriter.UnavailableNote);
            }

            var row = await db.PortfolioSnapshots.FirstAsync(x => x.Id == built.NewestSnapshotId && x.UserId == userId, cancellationToken);
            var cached = outcome.Summary is { } summary
                ? new CachedPortfolioSummary(summary.Text, summary.Model, summary.GeneratedUtc, null)
                : new CachedPortfolioSummary(null, null, null, outcome.Note);
            row.SummaryKey = built.Key;
            row.SummaryJson = JsonSerializer.Serialize(cached, PortfolioReportService.SnapshotJson);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The portfolio summary for user {UserId} was not written: {Error}", userId, ex.GetType().Name);
        }
    }
}
