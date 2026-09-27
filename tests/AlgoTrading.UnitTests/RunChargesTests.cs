using AlgoTrading.Api.Services;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The charges every "net" on the console takes off: per contract, at the lot
/// size the fill was booked at, one figure per run.
/// </summary>
public class RunChargesTests
{
    private const string Nifty = "NSE:NIFTY26SEP25000CE";
    private const string BankNifty = "NSE:BANKNIFTY26SEP55000PE";

    [Fact]
    public async Task Two_lots_bought_at_100_and_sold_at_120_on_a_75_lot_cost_88_33()
    {
        // The reviewer's trace, which the Python ledger gives as 88.3296.
        await using var db = NewDb();
        Fill(db, run: 1, Nifty, "BUY", lots: 2, price: 100m);
        Fill(db, run: 1, Nifty, "SELL", lots: 2, price: 120m);
        await db.SaveChangesAsync();

        var charges = await new RunCharges(db, new Lots((Nifty, 75))).ForRunAsync(1, default);

        Assert.Equal(88.33m, Math.Round(charges, 2));
    }

    [Fact]
    public async Task Each_contract_is_charged_at_its_own_lot_size()
    {
        // The manual book's run symbol is "MANUAL", and one book can hold
        // several underlyings: one lot size per run charged 10 NIFTY lots as
        // 10 units.
        await using var db = NewDb();
        Fill(db, run: 2, Nifty, "BUY", lots: 10, price: 150m);
        Fill(db, run: 2, Nifty, "SELL", lots: 10, price: 180m);
        Fill(db, run: 2, BankNifty, "BUY", lots: 1, price: 300m);
        await db.SaveChangesAsync();

        var lots = new Lots((Nifty, 65), (BankNifty, 30));
        decimal both = await new RunCharges(db, lots).ForRunAsync(2, default);

        var expected = Application.Risk.OptionCharges.For(
            buyTurnover: 150m * 10 * 65 + 300m * 1 * 30,
            sellTurnover: 180m * 10 * 65,
            orders: 3).Total;
        Assert.Equal(expected, both);
    }

    [Fact]
    public async Task A_book_with_index_and_crude_options_charges_each_at_its_own_rates()
    {
        const string Crude = "MCX:CRUDEOIL26OCT5500CE";
        await using var db = NewDb();
        Fill(db, run: 5, Nifty, "BUY", lots: 2, price: 100m);
        Fill(db, run: 5, Nifty, "SELL", lots: 2, price: 120m);
        Fill(db, run: 5, Crude, "BUY", lots: 2, price: 150m);
        Fill(db, run: 5, Crude, "SELL", lots: 2, price: 180m);
        await db.SaveChangesAsync();

        decimal both = await new RunCharges(db, new Lots((Nifty, 75), (Crude, 100))).ForRunAsync(5, default);

        var expected = Application.Risk.OptionCharges.For(15_000m, 18_000m, 2).Total
                     + Application.Risk.OptionCharges.For(30_000m, 36_000m, 2, Application.Risk.ChargeSchedule.McxOptions).Total;
        Assert.Equal(expected, both);
        Assert.Equal(88.33m + 98.74m, both);
    }

    [Fact]
    public async Task Unfilled_orders_cost_nothing_and_runs_are_kept_apart()
    {
        await using var db = NewDb();
        Fill(db, run: 3, Nifty, "BUY", lots: 1, price: 100m);
        db.PaperOrders.Add(new PaperOrder { SimulationRunId = 4, Symbol = Nifty, Side = "BUY", Quantity = 5, FillPrice = null });
        await db.SaveChangesAsync();

        var charges = await new RunCharges(db, new Lots((Nifty, 75))).ForRunsAsync(new long[] { 3, 4 }, default);

        Assert.True(charges[3] > 0m);
        Assert.False(charges.ContainsKey(4));
    }

    private static TradingDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"charges-{Guid.NewGuid():N}").Options);

    private static void Fill(TradingDbContext db, long run, string symbol, string side, int lots, decimal price) =>
        db.PaperOrders.Add(new PaperOrder
        {
            SimulationRunId = run, Symbol = symbol, Side = side, Quantity = lots,
            FillPrice = price, Status = "Filled", StrategyName = "Test",
        });

    private sealed class Lots(params (string Symbol, int Size)[] sizes) : ILotSizeResolver
    {
        private readonly Dictionary<string, int> _sizes = sizes.ToDictionary(x => x.Symbol, x => x.Size);

        public Task<LotSizeInfo> ResolveAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(Info(symbol));

        public Task<IReadOnlyDictionary<string, LotSizeInfo>> ResolveManyAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, LotSizeInfo>>(symbols.Distinct().ToDictionary(s => s, Info));

        public Task<LotSizeInfo> ResolveForUnderlyingAsync(string underlying, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LotSizeInfo(1, LotSizeInfo.SourceUnknown, underlying));

        private LotSizeInfo Info(string symbol) => _sizes.TryGetValue(symbol, out int size)
            ? new LotSizeInfo(size, LotSizeInfo.SourceMaster, symbol)
            : new LotSizeInfo(1, LotSizeInfo.SourceUnknown, symbol);
    }
}
