using System.Text.Json;
using AlgoTrading.Contracts.Portfolio;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Portfolio;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Api.Services.Portfolio;

/// <summary>A summary as it is cached on a snapshot: the paragraph, or the reason there is none.</summary>
public sealed record CachedPortfolioSummary(string? Text, string? Model, DateTime? GeneratedUtc, string? Note);

/// <summary>A report with what it was built from, so a sync can cache its summary on the right row.</summary>
/// <param name="Key">The snapshots it was built from: their ids, sorted, comma separated.</param>
public sealed record BuiltPortfolioReport(PortfolioReport Report, long NewestSnapshotId, string Key);

/// <summary>
/// One user's report and history, from their own snapshots only: every query here is filtered by the user id the
/// controller read from the sign-in token, so no request can reach another user's rows.
/// </summary>
public sealed class PortfolioReportService(
    TradingDbContext db,
    PortfolioPrices prices,
    PortfolioSessions sessions,
    IOptionsMonitor<PortfolioSettings> settings,
    TimeProvider? time = null)
{
    /// <summary>How the snapshots' JSON is written and read.</summary>
    public static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    public const string NoSummaryYetNote = "The written summary is written at each sync: sync again for one.";

    public const string SummaryOutdatedNote = "The written summary was left out: the figures have changed since it was written. The next sync writes a new one.";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The report, with its cached summary when that still says only what the report says; null when there is no snapshot.</summary>
    public async Task<PortfolioReport?> ReportAsync(long userId, CancellationToken cancellationToken)
    {
        var built = await BuildAsync(userId, cancellationToken);
        if (built is null) return null;

        var newest = await db.PortfolioSnapshots.AsNoTracking()
            .Where(s => s.Id == built.NewestSnapshotId && s.UserId == userId)
            .Select(s => new { s.SummaryKey, s.SummaryJson })
            .FirstOrDefaultAsync(cancellationToken);

        var cached = newest?.SummaryKey == built.Key ? ReadSummary(newest.SummaryJson) : null;
        var report = built.Report;
        if (cached?.Text is string text && cached.GeneratedUtc is DateTime at)
        {
            var summary = new PortfolioSummary(text, cached.Model ?? string.Empty, Checked: true, at);
            return PortfolioSummaryWriter.StillHolds(summary, report)
                ? report with { Summary = summary }
                : WithNote(report, SummaryOutdatedNote);
        }

        return WithNote(report, cached?.Note ?? NoSummaryYetNote);
    }

    /// <summary>The report from each linked broker's newest snapshot, without a summary; null when there is none.</summary>
    public async Task<BuiltPortfolioReport?> BuildAsync(long userId, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var links = await db.PortfolioConnections.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        if (links.Count == 0) return null;

        var newest = new List<PortfolioSnapshot>();
        var notes = new List<string>();
        foreach (var link in links.OrderBy(l => PortfolioBrokers.All.ToList().IndexOf(l.Broker)))
        {
            var snapshot = await db.PortfolioSnapshots.AsNoTracking()
                .Where(s => s.UserId == userId && s.Broker == link.Broker)
                .OrderByDescending(s => s.TakenUtc).ThenByDescending(s => s.Id)
                .FirstOrDefaultAsync(cancellationToken);
            string name = PortfolioBrokers.NameOf(link.Broker);
            if (snapshot is null)
            {
                notes.Add($"{name} is connected but has not been read yet: press Sync.");
                continue;
            }

            newest.Add(snapshot);
            if (PortfolioLinkService.StatusOf(link, now) == PortfolioConnectionStatus.Expired)
            {
                notes.Add($"The {name} sign-in has ended: its figures are from the last read. Connect {name} again to refresh them.");
            }
        }

        if (newest.Count == 0) return null;

        var snapshots = newest.Select(s =>
        {
            var link = links.First(l => l.Broker == s.Broker);
            return new PortfolioBrokerSnapshot(s.Broker, s.Id, s.TakenUtc, Read(s), link.LastError is null, link.LastError);
        }).ToList();

        var symbols = snapshots.SelectMany(s => s.Portfolio.Holdings)
            .Select(h => h.CanonicalSymbol).OfType<string>()
            .Append(PortfolioPrices.IndexSymbol)
            .Distinct().ToList();
        var through = sessions.LastCompletedSession(now);
        var from = through.AddDays(-Math.Max(30, settings.CurrentValue.HistoryLookbackDays));
        var closes = await prices.ClosesAsync(symbols, from, through, cancellationToken);
        var index = closes.TryGetValue(PortfolioPrices.IndexSymbol, out var nifty) ? nifty : [];

        var report = PortfolioReportBuilder.Build(new PortfolioReportInputs(now, snapshots, closes, index, Notes: notes));
        var key = string.Join(",", newest.Select(s => s.Id).Order());
        long newestId = newest.OrderByDescending(s => s.TakenUtc).ThenByDescending(s => s.Id).First().Id;
        return new BuiltPortfolioReport(report, newestId, key);
    }

    /// <summary>
    /// The value over time: for each IST day, the last snapshot of that day from each broker read that day, summed over
    /// the holdings that had a price. A day a broker was not read has no figures from it; nothing is carried forward.
    /// </summary>
    public async Task<IReadOnlyList<PortfolioHistoryPoint>> HistoryAsync(long userId, int days, CancellationToken cancellationToken)
    {
        int span = Math.Clamp(days, 1, Math.Max(1, settings.CurrentValue.RetentionDays));
        var since = _time.GetUtcNow().UtcDateTime.AddDays(-span);
        var rows = await db.PortfolioSnapshots.AsNoTracking()
            .Where(s => s.UserId == userId && s.TakenUtc >= since)
            .Select(s => new { s.Id, s.Broker, s.TakenUtc, s.HoldingsJson })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => IstTime.DateOf(DateTime.SpecifyKind(r.TakenUtc, DateTimeKind.Utc)))
            .OrderBy(g => g.Key)
            .Select(day =>
            {
                var holdings = day.GroupBy(r => r.Broker)
                    .Select(b => b.OrderBy(r => r.TakenUtc).ThenBy(r => r.Id).Last())
                    .SelectMany(r => Holdings(r.HoldingsJson))
                    .Where(h => h.Ltp is not null && h.Quantity > 0)
                    .ToList();
                decimal invested = Math.Round(holdings.Sum(h => h.Quantity * h.AvgPrice), 2);
                decimal current = Math.Round(holdings.Sum(h => h.Quantity * h.Ltp!.Value), 2);
                return new PortfolioHistoryPoint(day.Key, invested, current, current - invested);
            })
            .ToList();
    }

    public static BrokerPortfolio Read(PortfolioSnapshot snapshot) => new(
        Holdings(snapshot.HoldingsJson),
        Deserialize<List<BrokerPosition>>(snapshot.PositionsJson) ?? [],
        Deserialize<BrokerFunds>(snapshot.FundsJson));

    public static CachedPortfolioSummary? ReadSummary(string? json) => string.IsNullOrWhiteSpace(json) ? null : Deserialize<CachedPortfolioSummary>(json);

    private static List<BrokerHolding> Holdings(string json) => Deserialize<List<BrokerHolding>>(json) ?? [];

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, SnapshotJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PortfolioReport WithNote(PortfolioReport report, string note) =>
        report with { DataNotes = [.. report.DataNotes, note] };
}
