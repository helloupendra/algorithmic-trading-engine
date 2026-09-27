// src/AlgoTrading.Api/Services/RunPnlRecorder.cs
using AlgoTrading.Api.Controllers;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Writes each live run's P&amp;L once a minute (<see cref="RunPnlMinute"/>),
/// and a last row at the minute a run ended.
/// </summary>
/// <remarks>
/// <para>
/// Which runs: every LivePaper run whose row says Running or Stopping, manual
/// books included. The figures come from <see cref="RunPnl"/>, the code the
/// run history uses, so a minute's net is what the run card said at that
/// minute.
/// </para>
/// <para>
/// A manual book is open for good, and between sessions nothing in it moves,
/// so it gets a row only when its figures differ from its last one: a row a
/// minute through the night would store the same number nine hundred times.
/// A strategy run gets one every minute it is live, moved or not, so a gap
/// in its series means the recorder was not running, never "unchanged".
/// </para>
/// <para>
/// The final row: a run that ended in the last <see cref="FinalRowWindow"/>
/// is written at the minute of its <c>CompletedUtc</c>, with its figures
/// after the stop (the flatten realized its open legs; nothing is open).
/// That minute may already hold a row the live pass wrote seconds before the
/// stop; it is updated. Rewriting a final row on the next few passes changes
/// nothing, because a stopped run's figures no longer move, which is also why
/// no state is kept between passes and a restart loses nothing.
/// </para>
/// <para>
/// Cost per pass, whatever the number of runs: one query for the runs, one
/// for all their positions, one for the open legs' quotes, one for their
/// fills (the charges), one for the minutes already written, one save.
/// </para>
/// </remarks>
public sealed class RunPnlRecorder
{
    /// <summary>How far back a pass looks for runs that ended, to write their last row.</summary>
    public static readonly TimeSpan FinalRowWindow = TimeSpan.FromMinutes(10);

    private readonly TradingDbContext _dbContext;
    private readonly RunPnl _pnl;

    public RunPnlRecorder(TradingDbContext dbContext, RunPnl pnl)
    {
        _dbContext = dbContext;
        _pnl = pnl;
    }

    /// <summary>What one pass did.</summary>
    /// <param name="Live">Live runs sampled at this minute.</param>
    /// <param name="Ended">Runs whose final row was (re)written.</param>
    /// <param name="Written">Rows inserted or updated.</param>
    /// <param name="Unchanged">Manual books left alone because nothing in them moved.</param>
    public sealed record Pass(int Live, int Ended, int Written, int Unchanged);

    /// <summary>The minute an instant falls in: seconds and below cut off, as UTC.</summary>
    public static DateTime MinuteOf(DateTime utc)
        => new(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);

    public async Task<Pass> RecordAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var minute = MinuteOf(nowUtc);
        var endedSince = nowUtc - FinalRowWindow;

        var runs = await _dbContext.SimulationRuns.AsNoTracking()
            .Where(r => r.Mode == StrategyRunControl.LivePaperMode
                        && (r.Status == StrategyRunControl.RunStatusRunning
                            || r.Status == StrategyRunControl.RunStatusStopping
                            || (r.CompletedUtc != null && r.CompletedUtc >= endedSince && r.CompletedUtc <= nowUtc)))
            .Select(r => new { r.Id, r.Status, r.StrategyName, r.CompletedUtc })
            .ToListAsync(cancellationToken);
        if (runs.Count == 0) return new Pass(0, 0, 0, 0);

        var live = runs.Where(r => StrategyRunControl.IsOpenStatus(r.Status)).ToList();
        var ended = runs.Where(r => !StrategyRunControl.IsOpenStatus(r.Status) && r.CompletedUtc.HasValue).ToList();

        var liveIds = live.Select(r => r.Id).ToHashSet();
        var figures = await _pnl.FiguresAsync(runs.Select(r => r.Id).ToList(), liveIds, cancellationToken);

        var wanted = new List<RunPnlMinute>(runs.Count);
        wanted.AddRange(live.Select(r => Row(r.Id, minute, figures[r.Id])));
        wanted.AddRange(ended.Select(r => Row(r.Id, MinuteOf(r.CompletedUtc!.Value), figures[r.Id])));

        var books = live.Where(r => r.StrategyName == ManualOrdersController.BookStrategyName).Select(r => r.Id).ToList();
        int unchanged = 0;
        if (books.Count > 0)
        {
            var lastOfBook = await _dbContext.RunPnlMinutes.AsNoTracking()
                .Where(x => books.Contains(x.SimulationRunId))
                .GroupBy(x => x.SimulationRunId)
                .Select(g => g.OrderByDescending(x => x.AtUtc).First())
                .ToDictionaryAsync(x => x.SimulationRunId, cancellationToken);

            unchanged = wanted.RemoveAll(row =>
                lastOfBook.TryGetValue(row.SimulationRunId, out var last) && last.AtUtc < row.AtUtc && SameFigures(last, row));
        }

        int written = await UpsertAsync(wanted, cancellationToken);
        return new Pass(live.Count, ended.Count, written, unchanged);
    }

    /// <summary>
    /// Inserts the minutes not written yet and updates the ones that are: the
    /// (run, minute) pair is unique.
    /// </summary>
    private async Task<int> UpsertAsync(List<RunPnlMinute> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return 0;

        var runIds = rows.Select(x => x.SimulationRunId).Distinct().ToList();
        var minutes = rows.Select(x => x.AtUtc).Distinct().ToList();
        var existing = (await _dbContext.RunPnlMinutes
                .Where(x => runIds.Contains(x.SimulationRunId) && minutes.Contains(x.AtUtc))
                .ToListAsync(cancellationToken))
            .ToDictionary(x => (x.SimulationRunId, x.AtUtc));

        foreach (var row in rows)
        {
            if (existing.TryGetValue((row.SimulationRunId, row.AtUtc), out var stored))
            {
                stored.Realized = row.Realized;
                stored.Unrealized = row.Unrealized;
                stored.Charges = row.Charges;
                stored.Net = row.Net;
            }
            else
            {
                _dbContext.RunPnlMinutes.Add(row);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return rows.Count;
    }

    /// <summary>A row at the precision the table stores: rupees to the paisa.</summary>
    private static RunPnlMinute Row(long runId, DateTime atUtc, RunPnl.Figures f) => new()
    {
        SimulationRunId = runId,
        AtUtc = atUtc,
        Realized = Math.Round(f.Realized, 2),
        Unrealized = Math.Round(f.Unrealized, 2),
        Charges = Math.Round(f.Charges, 2),
        Net = Math.Round(f.Net, 2)
    };

    private static bool SameFigures(RunPnlMinute a, RunPnlMinute b)
        => a.Realized == b.Realized && a.Unrealized == b.Unrealized && a.Charges == b.Charges && a.Net == b.Net;
}

/// <summary>
/// Runs <see cref="RunPnlRecorder"/> once a minute, fifty seconds into it, so
/// a row stamped 10:15 holds the figures near the end of 10:15.
/// </summary>
public sealed class RunPnlRecorderService : BackgroundService
{
    /// <summary>How far into each minute a pass starts.</summary>
    public static readonly TimeSpan Offset = TimeSpan.FromSeconds(50);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RunPnlRecorderService> _logger;

    public RunPnlRecorderService(IServiceScopeFactory scopeFactory, ILogger<RunPnlRecorderService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>The next pass after <paramref name="nowUtc"/>: <see cref="Offset"/> into this minute, else into the next.</summary>
    public static DateTime NextPassUtc(DateTime nowUtc)
    {
        var at = RunPnlRecorder.MinuteOf(nowUtc) + Offset;
        return at > nowUtc ? at : at.AddMinutes(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = NextPassUtc(DateTime.UtcNow) - DateTime.UtcNow;
            try
            {
                if (wait > TimeSpan.Zero) await Task.Delay(wait, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await PassAsync(stoppingToken);
        }
    }

    private async Task PassAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var recorder = scope.ServiceProvider.GetRequiredService<RunPnlRecorder>();
            var pass = await recorder.RecordAsync(DateTime.UtcNow, ct);
            if (pass.Written > 0)
            {
                _logger.LogDebug("Run P&L minute: {Written} row(s) for {Live} live and {Ended} ended run(s); {Unchanged} book(s) unchanged.",
                    pass.Written, pass.Live, pass.Ended, pass.Unchanged);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // A missed minute is a gap in a chart, and says so; it must never
            // take the API down or stop the next minute being written.
            _logger.LogError(ex, "Run P&L minute pass failed; the next minute retries.");
        }
    }
}
