using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Patterns;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.AiTrader;

/// <summary>
/// The bar the AI Trader has to clear: a fixed rule anyone could follow, scored by code on each recorded day
/// it decided on (<c>ai_trader_baselines</c>). Next to it, doing nothing scores ₹0.
/// </summary>
/// <remarks>
/// The rule (<see cref="TrendRule"/>): at 11:00 IST, if NIFTY's last 5-minute close is above its EMA 20 and
/// EMA 50 with EMA 20 above EMA 50, buy the at-the-money call; below both with EMA 20 below, the put; otherwise
/// no trade. One lot, stop 30% under the entry, target 50% over it, squared off at 15:30. Only data from before
/// 11:00 decides: the bars up to 10:59 and the chain as of 11:00. It is bought at the ask of the first recorded
/// tick at or after 11:00 and then checked at each recorded minute's last tick at the price it could be sold
/// at, exactly as the shadow book checks the AI Trader's positions, with the same charges.
/// </remarks>
public sealed class AiTraderBaselineScorer(TradingDbContext db, IBaselineMarket market, IMarketSessionService sessions)
{
    public const string TrendRule = "nifty-trend-1100";
    public const string Underlying = "NIFTY";
    public const string Spot = "NSE:NIFTY50-INDEX";
    public static readonly TimeOnly DecideAt = new(11, 0);
    public const decimal StopFraction = 0.30m;
    public const decimal TargetFraction = 0.50m;

    /// <summary>The day's score under <see cref="TrendRule"/>: read when kept, else computed and kept. Null for a day that is not over.</summary>
    public async Task<AiTraderBaseline?> ForDayAsync(DateOnly day, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var kept = await db.AiTraderBaselines.AsNoTracking().FirstOrDefaultAsync(b => b.Day == day && b.Rule == TrendRule, cancellationToken);
        if (kept is not null) return kept;
        if (day >= IstTime.DateOf(nowUtc)) return null;

        var row = await ScoreAsync(day, cancellationToken);
        row.ComputedUtc = nowUtc;
        db.AiTraderBaselines.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return row;
    }

    private async Task<AiTraderBaseline> ScoreAsync(DateOnly day, CancellationToken cancellationToken)
    {
        var row = new AiTraderBaseline { Day = day, Rule = TrendRule, Underlying = Underlying };
        var decideUtc = IstTime.FromIst(day.ToDateTime(DecideAt));
        var closeUtc = sessions.GetSessionInfo(decideUtc, "NSE", "FO").SessionCloseUtc;

        var minutes = await market.MinutesBeforeAsync(Spot, decideUtc, cancellationToken);
        var (option, why) = Direction(minutes, decideUtc);
        if (option is null) return Note(row, why);

        var contract = await market.AtTheMoneyAsync(Underlying, option, decideUtc, cancellationToken);
        if (contract is null) return Note(row, $"{why}, but no {option} at the money was recorded in the chain at 11:00.");
        row.OptionType = option;
        row.Symbol = contract.Symbol;
        row.LotSize = contract.LotSize;
        row.Lots = 1;

        var ticks = await db.LiveTicks.AsNoTracking()
            .Where(t => t.Symbol == contract.Symbol && t.ReceivedUtc >= decideUtc && t.ReceivedUtc < closeUtc && t.SourceKey != "mock")
            .OrderBy(t => t.ReceivedUtc)
            .Select(t => new BaselineTick(t.ReceivedUtc, t.LastTradedPrice, t.BidPrice, t.AskPrice))
            .ToListAsync(cancellationToken);
        var entry = ticks.FirstOrDefault(t => t.Ask is > 0 || t.Last is > 0);
        if (entry is null) return Note(row, $"{why}, but {contract.Symbol} has no recorded ticks after 11:00.");

        decimal price = entry.Ask is > 0 ? entry.Ask.Value : entry.Last!.Value;
        row.EntryUtc = entry.AtUtc;
        row.EntryPrice = price;
        row.StopLoss = Math.Round(price * (1 - StopFraction), 2);
        row.Target = Math.Round(price * (1 + TargetFraction), 2);

        // No price recorded after the entry: closed where it was bought, never at nothing.
        var exit = Walk(ticks.Where(t => t.AtUtc > entry.AtUtc).ToList(), row.StopLoss.Value, row.Target.Value)
                   ?? (entry.AtUtc, price, AiTraderShadowBook.SessionClose);
        row.ExitUtc = exit.AtUtc;
        row.ExitPrice = exit.Price;
        row.ExitReason = exit.Reason;
        int units = row.Lots * row.LotSize;
        row.Charges = AiTraderShadowBook.Charges(row.Symbol, price, exit.Price, units);
        row.NetPnl = Math.Round((exit.Price - price) * units - row.Charges, 2);
        row.Note = $"{why}: bought {contract.Symbol} at {price:0.##}, {exit.Reason} at {exit.Price:0.##}.";
        return row;
    }

    private static AiTraderBaseline Note(AiTraderBaseline row, string why)
    {
        row.Note = why.Length <= 500 ? why : why[..499] + "…";
        return row;
    }

    /// <summary>
    /// CE when the last 5-minute close before <paramref name="decideUtc"/> is above EMA 20 and EMA 50 with EMA 20 above
    /// EMA 50, PE when below both with EMA 20 below, else no trade; with the reason. The minute bars are oldest first and
    /// only those that began before the decision are used.
    /// </summary>
    public static (string? Option, string Why) Direction(IReadOnlyList<LiveBarResponse> minutesOldestFirst, DateTime decideUtc)
    {
        var fives = MarketBriefBuilder.FiveMinute(minutesOldestFirst.Where(b => b.BarStartUtc < decideUtc));
        if (fives.Count < 50) return (null, $"Only {fives.Count} five-minute bars before 11:00, fewer than EMA 50 needs.");
        var closes = fives.Select(b => (double)b.Close).ToList();
        double? ema20 = IndicatorMath.Ema(closes, 20).LastOrDefault(v => v is not null);
        double? ema50 = IndicatorMath.Ema(closes, 50).LastOrDefault(v => v is not null);
        if (ema20 is not double e20 || ema50 is not double e50) return (null, "EMA 20 or EMA 50 could not be computed.");
        double last = closes[^1];
        string facts = $"NIFTY {last:0.##}, EMA20 {e20:0.#}, EMA50 {e50:0.#}";
        return last > e20 && last > e50 && e20 > e50 ? ("CE", $"{facts}: above both, EMA20 above")
            : last < e20 && last < e50 && e20 < e50 ? ("PE", $"{facts}: below both, EMA20 below")
            : (null, $"{facts}: no clear trend at 11:00, no trade");
    }

    /// <summary>
    /// The exit, checking each recorded minute's last tick at the price it could be sold at (the bid, else the last
    /// trade less half a spread): at or under the stop, at or over the target, else the last tick before the close.
    /// Null when no tick after the entry carries a price.
    /// </summary>
    public static (DateTime AtUtc, decimal Price, string Reason)? Walk(IReadOnlyList<BaselineTick> ticksAfterEntry, decimal stop, decimal target)
    {
        (DateTime, decimal)? last = null;
        foreach (var minute in ticksAfterEntry.GroupBy(t => new DateTime(t.AtUtc.Ticks - t.AtUtc.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc)).OrderBy(g => g.Key))
        {
            var t = minute.Last();
            if (AiTraderShadowBook.SellPrice(new QuoteSnapshot(t.Last, t.Bid, t.Ask, t.AtUtc), t.AtUtc) is not decimal sell) continue;
            if (sell <= stop) return (t.AtUtc, sell, AiTraderShadowBook.Stopped);
            if (sell >= target) return (t.AtUtc, sell, AiTraderShadowBook.TargetHit);
            last = (t.AtUtc, sell);
        }

        return last is var (at, price) ? (at, price, AiTraderShadowBook.SessionClose) : null;
    }
}

public sealed record BaselineTick(DateTime AtUtc, decimal? Last, decimal? Bid, decimal? Ask);

public sealed record BaselineContract(string Symbol, decimal Strike, int LotSize);

/// <summary>The recorded market the baseline reads (<see cref="BaselineMarket"/>; a fake in tests).</summary>
public interface IBaselineMarket
{
    /// <summary>The 1-minute bars that began before <paramref name="beforeUtc"/>, oldest first, enough for EMA 50 on 5-minute bars.</summary>
    Task<IReadOnlyList<LiveBarResponse>> MinutesBeforeAsync(string symbol, DateTime beforeUtc, CancellationToken cancellationToken);

    /// <summary>The nearest expiry's at-the-money contract of that side, as the chain was recorded at <paramref name="asOfUtc"/>.</summary>
    Task<BaselineContract?> AtTheMoneyAsync(string underlying, string optionType, DateTime asOfUtc, CancellationToken cancellationToken);
}

public sealed class BaselineMarket(ILiveDataService live, OptionChainService chains) : IBaselineMarket
{
    public async Task<IReadOnlyList<LiveBarResponse>> MinutesBeforeAsync(string symbol, DateTime beforeUtc, CancellationToken cancellationToken)
    {
        // The minutes that had ended by the decision: the read leaves out the minute its moment falls in (11:00,
        // still open then) and keeps 10:59, which closed at 11:00. A tick earlier would lose 10:59 as well.
        var newestFirst = await live.GetBarsUntilAsync(symbol, "1m", 1000, beforeUtc, null, cancellationToken);
        return newestFirst.OrderBy(b => b.BarStartUtc).ToList();
    }

    public async Task<BaselineContract?> AtTheMoneyAsync(string underlying, string optionType, DateTime asOfUtc, CancellationToken cancellationToken)
    {
        var view = await chains.GetViewAsync(underlying, null, asOfUtc, cancellationToken);
        decimal? atm = view.AtTheMoneyStrike ?? view.Strikes.FirstOrDefault(s => s.IsAtTheMoney)?.StrikePrice;
        var row = view.Strikes.FirstOrDefault(s => s.StrikePrice == atm);
        var leg = optionType == "CE" ? row?.Call : row?.Put;
        int lot = view.Header?.LotSize ?? 0;
        return leg is null || string.IsNullOrWhiteSpace(leg.Symbol) || lot <= 0 ? null : new BaselineContract(leg.Symbol, row!.StrikePrice, lot);
    }
}
