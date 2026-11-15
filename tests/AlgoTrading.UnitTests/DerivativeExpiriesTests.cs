using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The option expiries an underlying lists: live callers see the tradable ones, a backtest
/// (includeHistory) also the expired weeklies the master keeps as disabled rows.
/// </summary>
public class DerivativeExpiriesTests
{
    private static Instrument Option(string expiry, bool enabled, string type = "CE") => new()
    {
        Symbol = $"NSE:NIFTY{expiry}{type}",
        Exchange = "NSE",
        Segment = "FO",
        InstrumentType = type,
        OptionType = type,
        Underlying = "NIFTY",
        StrikePrice = 25000m,
        ExpiryDate = DateOnly.Parse(expiry),
        IsEnabled = enabled,
    };

    private static async Task<DerivativesInstrumentService> Service()
    {
        var db = AiTestKit.NewDb();
        db.Instruments.AddRange(
            Option("2026-10-27", enabled: false),
            Option("2026-11-03", enabled: false, type: "PE"),
            Option("2026-11-17", enabled: true),
            new Instrument
            {
                Symbol = "NSE:NIFTY26NOVFUT", Exchange = "NSE", Segment = "FO", InstrumentType = "FUT",
                Underlying = "NIFTY", ExpiryDate = DateOnly.Parse("2026-11-24"), IsEnabled = false,
            });
        await db.SaveChangesAsync();
        return new DerivativesInstrumentService(db);
    }

    [Fact]
    public async Task Live_callers_see_only_enabled_option_expiries()
    {
        var expiries = await (await Service()).GetExpiriesAsync("NIFTY");

        Assert.Equal(new[] { DateOnly.Parse("2026-11-17") }, expiries.Select(x => x.ExpiryDate));
    }

    [Fact]
    public async Task History_adds_the_expired_option_expiries_the_master_disabled()
    {
        var expiries = await (await Service()).GetExpiriesAsync("NIFTY", includeHistory: true);

        Assert.Equal(
            new[] { DateOnly.Parse("2026-10-27"), DateOnly.Parse("2026-11-03"), DateOnly.Parse("2026-11-17") },
            expiries.Select(x => x.ExpiryDate));
    }
}
