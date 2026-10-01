using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Risk;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// The AI Trader's shadow book: the positions it would hold had its allowed buys been placed, in shadow mode
/// and in a market replay, where nothing is placed. Without it a shadow day cannot be scored, and the model,
/// seeing an empty book, buys the same contract again on every look.
/// </summary>
/// <remarks>
/// Fills follow the desk's paper fills (<see cref="PaperFillPricing"/>): bought at the ask the decision saw,
/// sold at the bid, else the last trade less half a spread. Every minute each open position is checked at that
/// sell price: at or under its stop it is stopped, at or over its target it is taken, and at the session's close
/// (15:30 IST) it is squared off. A touch between two checks is missed, and a stop fills at the price of the
/// check that saw it, which can be under the stop. Charges are the desk's for an index option round trip.
/// </remarks>
public sealed class AiTraderShadowBook(TradingDbContext db, IAiTraderQuotes quotes, IMarketSessionService sessions)
{
    private static readonly PaperFillOptions Fills = new();

    public const string Stopped = "stop";
    public const string TargetHit = "target";
    public const string ExitedByIt = "exit";
    public const string SessionClose = "close";
    public const string ReplayEnded = "replay-ended";

    /// <summary>Opens the position an allowed buy names, at the price the decision saw.</summary>
    public async Task<AiTraderShadowPosition> OpenAsync(AiTraderDecision decision, AiTraderPlan plan, AiTraderContract contract, CancellationToken cancellationToken)
    {
        var position = new AiTraderShadowPosition
        {
            CreatedUtc = DateTime.UtcNow,
            DecisionId = decision.Id,
            Mode = decision.Mode,
            ReplaySessionId = decision.ReplaySessionId,
            Day = decision.Day,
            Symbol = contract.Symbol,
            Underlying = contract.Underlying,
            OptionType = contract.OptionType,
            Strike = contract.Strike,
            Expiry = contract.Expiry,
            Lots = plan.Lots ?? 1,
            LotSize = contract.LotSize,
            EntryUtc = decision.ClockUtc,
            EntryPrice = contract.Ask,
            StopLoss = plan.StopLoss ?? 0m,
            Target = plan.Target ?? 0m,
            MarkPrice = contract.Ask,
            MarkUtc = decision.ClockUtc,
        };
        db.AiTraderShadowPositions.Add(position);
        await db.SaveChangesAsync(cancellationToken);
        return position;
    }

    /// <summary>Closes one of its open positions on its own decision, at the price it could be sold at now.</summary>
    public async Task<AiTraderShadowPosition?> ExitAsync(long positionId, long? replaySessionId, DateTime clockUtc, CancellationToken cancellationToken)
    {
        var position = await db.AiTraderShadowPositions.FirstOrDefaultAsync(
            p => p.Id == positionId && p.ReplaySessionId == replaySessionId && p.ExitUtc == null, cancellationToken);
        if (position is null) return null;
        var quote = await quotes.QuoteAsync(position, clockUtc, replaySessionId is not null, cancellationToken);
        Close(position, SellPrice(quote, clockUtc) ?? position.MarkPrice ?? position.EntryPrice, clockUtc, ExitedByIt);
        await db.SaveChangesAsync(cancellationToken);
        return position;
    }

    /// <summary>
    /// The minute check of the open positions in one book (a replay's, or the live shadow book when
    /// <paramref name="replaySessionId"/> is null) at <paramref name="clockUtc"/>: marks each, and closes it at
    /// its stop, its target, or the session's close. A live shadow position left from an earlier day closes at
    /// its last mark. Returns how many closed.
    /// </summary>
    public async Task<int> CheckAsync(DateTime clockUtc, long? replaySessionId, CancellationToken cancellationToken)
    {
        var open = await db.AiTraderShadowPositions
            .Where(p => p.ExitUtc == null && p.ReplaySessionId == replaySessionId)
            .ToListAsync(cancellationToken);
        if (open.Count == 0) return 0;

        int closed = 0;
        var today = IstTime.DateOf(clockUtc);
        foreach (var p in open)
        {
            if (p.Day < today)
            {
                Close(p, p.MarkPrice ?? p.EntryPrice, p.MarkUtc ?? p.EntryUtc, SessionClose);
                closed++;
                continue;
            }

            var quote = await quotes.QuoteAsync(p, clockUtc, replaySessionId is not null, cancellationToken);
            decimal? sell = SellPrice(quote, clockUtc);
            if (sell is decimal s)
            {
                p.MarkPrice = quote?.LastTradedPrice is > 0 ? quote.Value.LastTradedPrice : s;
                p.MarkUtc = clockUtc;
            }

            bool atClose = clockUtc >= sessions.GetSessionInfo(clockUtc, "NSE", "FO").SessionCloseUtc;
            string? reason = atClose ? SessionClose
                : sell is decimal low && low <= p.StopLoss ? Stopped
                : sell is decimal high && high >= p.Target ? TargetHit
                : null;
            if (reason is null) continue;
            Close(p, sell ?? p.MarkPrice ?? p.EntryPrice, clockUtc, reason);
            closed++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return closed;
    }

    /// <summary>Closes, at their last marks, the open positions of every replay but <paramref name="activeReplayId"/>: a replay that ended.</summary>
    public async Task<int> EndReplaysAsync(long? activeReplayId, CancellationToken cancellationToken)
    {
        var orphans = await db.AiTraderShadowPositions
            .Where(p => p.ExitUtc == null && p.ReplaySessionId != null && p.ReplaySessionId != activeReplayId)
            .ToListAsync(cancellationToken);
        foreach (var p in orphans) Close(p, p.MarkPrice ?? p.EntryPrice, p.MarkUtc ?? p.EntryUtc, ReplayEnded);
        if (orphans.Count > 0) await db.SaveChangesAsync(cancellationToken);
        return orphans.Count;
    }

    /// <summary>
    /// <paramref name="live"/> (the account's kill switch and trading day) with the shadow book's day in place of
    /// the account's: its open positions, net after charges and the trades it opened. Strategy runs are not
    /// simulated in shadow, so none are running.
    /// </summary>
    public async Task<AiTraderBook> ReadAsync(AiTraderBook live, long? replaySessionId, CancellationToken cancellationToken)
    {
        var day = IstTime.DateOf(live.ClockUtc);
        var positions = await Positions(day, replaySessionId).AsNoTracking().ToListAsync(cancellationToken);
        var open = positions.Where(p => p.ExitUtc is null)
            .Select(p => new AiTraderOpenPosition(p.Id, p.Symbol, p.Lots, p.EntryPrice, p.EntryPrice * p.Units, p.MarkPrice,
                p.StopLoss, p.Target, Unrealized(p), p.Underlying, p.OptionType))
            .ToList();
        return live with { NetToday = Net(positions), OpenedToday = positions.Count, Open = open, Runs = [] };
    }

    /// <summary>A day's live shadow positions, or a replay's (of any day).</summary>
    public IQueryable<AiTraderShadowPosition> Positions(DateOnly day, long? replaySessionId) => replaySessionId is long r
        ? db.AiTraderShadowPositions.Where(p => p.ReplaySessionId == r)
        : db.AiTraderShadowPositions.Where(p => p.ReplaySessionId == null && p.Day == day);

    /// <summary>Net after charges: closed positions as closed, open ones as if sold at their marks now.</summary>
    public static decimal Net(IEnumerable<AiTraderShadowPosition> positions) => Math.Round(positions.Sum(p => p.ExitUtc is null
        ? Unrealized(p) - Charges(p.Symbol, p.EntryPrice, p.MarkPrice ?? p.EntryPrice, p.Units)
        : p.NetPnl ?? 0m), 2);

    public static decimal Unrealized(AiTraderShadowPosition p) => Math.Round(((p.MarkPrice ?? p.EntryPrice) - p.EntryPrice) * p.Units, 2);

    /// <summary>The desk's charges for one buy and one sell of an index option (<see cref="OptionCharges"/>).</summary>
    public static decimal Charges(string symbol, decimal entry, decimal exit, int units) =>
        OptionCharges.For(entry * units, exit * units, 2, ChargeSchedule.ForSymbol(symbol)).Total;

    /// <summary>The price it could sell at: the bid, else the last trade less half a spread, as a paper fill.</summary>
    public static decimal? SellPrice(QuoteSnapshot? quote, DateTime clockUtc) =>
        quote is { } q ? PaperFillPricing.FromQuote("SELL", q, Fills, clockUtc)?.Price : null;

    private static void Close(AiTraderShadowPosition p, decimal price, DateTime atUtc, string reason)
    {
        p.ExitPrice = price;
        p.ExitUtc = atUtc;
        p.ExitReason = reason;
        p.MarkPrice = price;
        p.MarkUtc = atUtc;
        p.Charges = Charges(p.Symbol, p.EntryPrice, price, p.Units);
        p.NetPnl = Math.Round((price - p.EntryPrice) * p.Units - p.Charges, 2);
    }
}

/// <summary>An option's quote for the shadow book (<see cref="AiTraderQuotes"/>; a fake in tests).</summary>
public interface IAiTraderQuotes
{
    Task<QuoteSnapshot?> QuoteAsync(AiTraderShadowPosition position, DateTime clockUtc, bool replay, CancellationToken cancellationToken);
}

/// <summary>
/// An option's quote at a moment. In a replay: the replay's own quote, else the recorded chain as of the
/// replay's clock. Live: the feed's quote when it is under three minutes old, else the live chain (the minute
/// capture with fresh quotes laid over it). A shadow position is never put on the feed: it places nothing.
/// </summary>
public sealed class AiTraderQuotes(TradingDbContext db, OptionChainService chains, ILogger<AiTraderQuotes> logger, IMarketReplayBook? replayBook = null) : IAiTraderQuotes
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(3);

    public async Task<QuoteSnapshot?> QuoteAsync(AiTraderShadowPosition position, DateTime clockUtc, bool replay, CancellationToken cancellationToken)
    {
        if (replay)
        {
            if (replayBook?.Quote(position.Symbol) is { } q && q.LastTradedPrice is > 0)
            {
                return new QuoteSnapshot(q.LastTradedPrice, q.BidPrice, q.AskPrice, q.ExchangeTimestampUtc ?? clockUtc);
            }
        }
        else
        {
            var live = await db.LiveQuotesLatest.AsNoTracking()
                .Where(x => x.Symbol == position.Symbol)
                .Select(x => new { x.LastTradedPrice, x.BidPrice, x.AskPrice, x.UpdatedUtc })
                .FirstOrDefaultAsync(cancellationToken);
            if (live is not null && live.LastTradedPrice is > 0 && clockUtc - live.UpdatedUtc < FreshFor)
            {
                return new QuoteSnapshot(live.LastTradedPrice, live.BidPrice, live.AskPrice, live.UpdatedUtc);
            }
        }

        try
        {
            var view = await chains.GetViewAsync(position.Underlying, position.Expiry, replay ? clockUtc : null, cancellationToken);
            var leg = view.Strikes.Select(s => position.OptionType == "CE" ? s.Call : s.Put)
                .FirstOrDefault(l => l is not null && l.Symbol == position.Symbol);
            if (leg is null || !(leg.LastTradedPrice is > 0 || leg.BidPrice is > 0)) return null;

            // The price is as old as the capture (or the live quote laid over it), never "now": with the recorder
            // stopped at 11:00, its 11:00 prices would stop, take and mark positions all afternoon as if current.
            // Older than three minutes it is no price this minute, as on the chain page. After the close the
            // session's last capture is its closing price.
            var at = leg.IsLive && leg.QuoteUpdatedUtc is DateTime quoted ? quoted : view.AsOfUtc;
            var close = IstTime.FromIst(IstTime.DateOf(clockUtc).ToDateTime(TimeOnly.FromTimeSpan(IstTime.SessionClose)));
            return (clockUtc < close ? clockUtc : close) - at <= FreshFor
                ? new QuoteSnapshot(leg.LastTradedPrice, leg.BidPrice, leg.AskPrice, at)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No price this minute: the position keeps its last mark and is checked again at the next.
            logger.LogWarning(ex, "AI Trader shadow book: no chain price for {Symbol}", position.Symbol);
            return null;
        }
    }
}
