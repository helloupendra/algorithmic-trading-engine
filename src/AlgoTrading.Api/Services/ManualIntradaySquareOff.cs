// src/AlgoTrading.Api/Services/ManualIntradaySquareOff.cs
using System.Globalization;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Squares off the manual book's intraday positions — those without the
/// carry-forward tick — at the close of their own exchange.
/// </summary>
/// <remarks>
/// <para>
/// 27 Sep, the owner: "if I want to carry forward, there should be a tick
/// there and ticking it is enough." So a hand-placed position is intraday
/// unless ticked, as with a broker's MIS: an NSE or BSE position is squared
/// off at 15:30 IST, an MCX one at the MCX close (23:30 IST while New York is
/// on summer time, 23:55 otherwise) — the per-exchange close strategy runs
/// already stop at (<see cref="MarketCloseRules"/>). A ticked position is held
/// overnight and is never touched here. Until then, from earlier on 27 Sep,
/// every manual position was carried; the migration that added the tick
/// ticked the positions held at that moment, so the deploy squares off
/// nothing anybody had chosen to keep.
/// </para>
/// <para>
/// The rule needs no memory, so a restart cannot skip it: a position is due
/// when the latest close of its exchange came after it became intraday — when
/// it was opened, or, if its tick was cleared later, when that happened. An
/// API that was down at 15:30 squares the day's intraday positions off on its
/// first minute back. A position carried from yesterday and unticked this
/// morning is intraday for today's close, not squared off the moment the tick
/// is cleared; and one opened after its market closed (a limit order in the
/// evening) waits for the next close, as a run started then does.
/// </para>
/// <para>
/// It runs in the API, every minute, from <see cref="MarketHoursService"/>.
/// <c>scripts/market-close.sh</c> still leaves the book alone: at 23:35 it
/// would square off carried positions too, and it cannot tell them apart.
/// </para>
/// </remarks>
public sealed class ManualIntradaySquareOff
{
    /// <summary>Who the square-off is attributed to on the activity feed.</summary>
    public const string By = "market-hours";

    /// <summary>An open, unticked manual-book position, as the rule needs to see it.</summary>
    public sealed record Candidate(long PositionId, long RunId, string Symbol, DateTime OpenedUtc, DateTime? CarryForwardChangedUtc);

    /// <summary>A position whose exchange has closed since it became intraday.</summary>
    public sealed record Due(long PositionId, long RunId, string Exchange, DateTime ClosedAtUtc, string Reason);

    private readonly TradingDbContext _dbContext;
    private readonly IPaperTradingService _paperTrading;
    private readonly IMarketSessionService _sessions;
    private readonly ILogger<ManualIntradaySquareOff> _logger;

    public ManualIntradaySquareOff(
        TradingDbContext dbContext,
        IPaperTradingService paperTrading,
        IMarketSessionService sessions,
        ILogger<ManualIntradaySquareOff> logger)
    {
        _dbContext = dbContext;
        _paperTrading = paperTrading;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>
    /// Squares off every due intraday position in every open manual book.
    /// Idempotent: a position once squared off is closed and never due again.
    /// Returns how many were closed.
    /// </summary>
    public async Task<int> SquareOffDueAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var books = _dbContext.SimulationRuns.AsNoTracking()
            .Where(x => x.StrategyName == ManualOrdersController.BookStrategyName
                        && x.Mode == StrategyRunControl.LivePaperMode
                        && x.Status == StrategyRunControl.RunStatusRunning)
            .Select(x => x.Id);

        var candidates = await _dbContext.PaperPositions.AsNoTracking()
            .Where(x => x.Status == "Open" && !x.CarryForward && x.Quantity > 0 && books.Contains(x.SimulationRunId))
            .OrderBy(x => x.Id)
            .Select(x => new Candidate(x.Id, x.SimulationRunId, x.Symbol, x.OpenedUtc, x.CarryForwardChangedUtc))
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0) return 0;

        var due = DuePositions(_sessions, nowUtc, candidates);
        int closed = 0;

        // One close per book and reason, so an NSE and an MCX position squared
        // off in the same minute (a restart late in the evening) each say which
        // close it was.
        foreach (var batch in due.GroupBy(x => (x.RunId, x.Reason)))
        {
            try
            {
                int n = await _paperTrading.CloseIntradayPositionsAsync(
                    batch.Key.RunId, batch.Select(x => x.PositionId), batch.Key.Reason, By, CancellationToken.None);
                closed += n;
                if (n > 0)
                {
                    _logger.LogInformation("Manual book {RunId}: {Count} intraday position(s) squared off — {Reason}.",
                        batch.Key.RunId, n, batch.Key.Reason);
                }
            }
            catch (Exception ex)
            {
                // Asked again a minute later by the rule itself.
                _logger.LogError(ex, "Manual book {RunId}: intraday square-off failed ({Reason}); retrying next minute.",
                    batch.Key.RunId, batch.Key.Reason);
            }
        }

        return closed;
    }

    /// <summary>
    /// The positions due now: each exchange's latest close is asked once, and a
    /// position is due when that close came after it became intraday.
    /// </summary>
    public static IReadOnlyList<Due> DuePositions(IMarketSessionService sessions, DateTime nowUtc, IEnumerable<Candidate> candidates)
    {
        var closes = new Dictionary<string, DateTime?>(StringComparer.Ordinal);
        var due = new List<Due>();

        foreach (var c in candidates)
        {
            string exchange = MarketCloseRules.ExchangeOf(null, c.Symbol);
            if (!closes.TryGetValue(exchange, out var close))
            {
                close = MarketCloseRules.LastCloseUtc(sessions, nowUtc, exchange);
                closes[exchange] = close;
            }

            DateTime intradaySince = c.CarryForwardChangedUtc is { } changed && changed > c.OpenedUtc
                ? changed
                : c.OpenedUtc;

            if (close is DateTime closedAt && intradaySince < closedAt)
            {
                due.Add(new Due(c.PositionId, c.RunId, exchange, closedAt, ReasonFor(exchange, closedAt)));
            }
        }

        return due;
    }

    /// <summary>
    /// "Intraday — squared off at the close (15:30 IST)", or at the MCX close
    /// with the time that actually applied.
    /// </summary>
    public static string ReasonFor(string exchange, DateTime closedAtUtc)
    {
        var at = IstTime.ToIst(closedAtUtc).ToString("HH:mm", CultureInfo.InvariantCulture);
        return exchange == MarketCloseRules.Mcx
            ? $"Intraday — squared off at the MCX close ({at} IST)"
            : $"Intraday — squared off at the close ({at} IST)";
    }
}
