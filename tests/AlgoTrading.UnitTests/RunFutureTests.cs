using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The future a commodity run prices itself against. MCX crude options expire on
/// about the 17th and their future on about the 21st, and for those days the run
/// used to take its ATM strike and its VWAP and EMA from the expiring future while
/// trading next month's options.
/// </summary>
public class RunFutureTests
{
    private const string Expiring = "MCX:CRUDEOIL26SEPFUT";
    private const string NextMonth = "MCX:CRUDEOIL26OCTFUT";

    // The same UTC date the service and the runner read.
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static TradingDbContext Db() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"run-future-{Guid.NewGuid():N}").Options);

    private static Instrument Future(string symbol, DateOnly expiry, string underlying = "CRUDEOIL", bool enabled = true) => new()
    {
        Symbol = symbol, Exchange = "MCX", Segment = "COM", InstrumentType = "FUT", Underlying = underlying,
        ExpiryDate = expiry, IsEnabled = enabled,
    };

    private static Instrument Option(DateOnly expiry, string type = "CE", bool enabled = true) => new()
    {
        Symbol = $"MCX:CRUDEOIL{expiry:yyMMdd}5500{type}", Exchange = "MCX", Segment = "COM", InstrumentType = type, OptionType = type,
        Underlying = "CRUDEOIL", ExpiryDate = expiry, StrikePrice = 5500m, IsEnabled = enabled,
    };

    private static async Task<string?> FutureFor(params Instrument[] instruments)
    {
        await using var db = Db();
        db.Instruments.AddRange(instruments);
        await db.SaveChangesAsync();
        return await new DerivativesInstrumentService(db).GetFutureOfNearestOptionsAsync(" crudeoil ");
    }

    [Fact]
    public async Task Before_the_option_expiry_it_is_the_nearest_future()
    {
        var symbol = await FutureFor(
            Future(Expiring, Today.AddDays(9)), Future(NextMonth, Today.AddDays(39)),
            Option(Today.AddDays(5)), Option(Today.AddDays(35)));

        Assert.Equal(Expiring, symbol);
    }

    [Fact]
    public async Task On_the_option_expiry_it_is_still_the_future_those_options_are_written_on()
    {
        // The runner trades the same-day contract, so the lines stay on its future.
        var symbol = await FutureFor(
            Future(Expiring, Today.AddDays(4)), Future(NextMonth, Today.AddDays(34)),
            Option(Today), Option(Today.AddDays(30)));

        Assert.Equal(Expiring, symbol);
    }

    [Theory]
    [InlineData(1)]   // the day after the option expiry
    [InlineData(4)]   // the future's own last day
    public async Task After_the_option_expiry_it_is_next_months_future(int daysSinceOptionExpiry)
    {
        var symbol = await FutureFor(
            Future(Expiring, Today.AddDays(4 - daysSinceOptionExpiry)), Future(NextMonth, Today.AddDays(34)),
            Option(Today.AddDays(-daysSinceOptionExpiry)), Option(Today.AddDays(30 - daysSinceOptionExpiry)));

        Assert.Equal(NextMonth, symbol);
    }

    [Fact]
    public async Task Once_the_old_future_has_expired_nothing_changes()
    {
        var symbol = await FutureFor(
            Future(Expiring, Today.AddDays(-1)), Future(NextMonth, Today.AddDays(29)),
            Option(Today.AddDays(25)));

        Assert.Equal(NextMonth, symbol);
    }

    [Fact]
    public async Task The_mini_contract_is_never_taken_for_the_full_one()
    {
        var symbol = await FutureFor(
            Future("MCX:CRUDEOILM26SEPFUT", Today.AddDays(2), underlying: "CRUDEOILM"), Future(Expiring, Today.AddDays(9)),
            Option(Today.AddDays(5)));

        Assert.Equal(Expiring, symbol);
    }

    [Fact]
    public async Task A_disabled_expiry_is_not_the_one_the_runner_trades()
    {
        // The runner lists enabled contracts only, and so does this.
        var symbol = await FutureFor(
            Future(Expiring, Today.AddDays(4)), Future(NextMonth, Today.AddDays(34)),
            Option(Today.AddDays(2), enabled: false), Option(Today.AddDays(30)));

        Assert.Equal(NextMonth, symbol);
    }

    [Fact]
    public async Task With_no_future_for_those_options_there_is_no_answer_rather_than_the_wrong_month()
    {
        Assert.Null(await FutureFor(Future(Expiring, Today.AddDays(2)), Option(Today.AddDays(29))));
    }
}
