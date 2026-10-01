using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.AiTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>The brief built from the desk's own records, as of a moment: here, its option chains.</summary>
public class MarketBriefBuilderTests
{
    // Wednesday 30 Sep 2026; NIFTY's weekly expiry was Tuesday 29 Sep.
    private static readonly DateOnly Wednesday = new(2026, 9, 30);
    private static readonly DateOnly Tuesday = new(2026, 9, 29);

    [Fact]
    public async Task Before_the_days_first_chain_capture_no_chain_is_offered_and_the_brief_says_so()
    {
        var db = NewDb();
        // The last capture before Wednesday's 09:20 is Tuesday's 15:29, of the series that expired on Tuesday.
        Chain(db, IstTime.FromIst(Tuesday.ToDateTime(new TimeOnly(15, 29))), expiry: Tuesday);

        var brief = await Builder(db).BuildAsync(IstTime.FromIst(Wednesday.ToDateTime(new TimeOnly(9, 20))), replay: true, default);

        Assert.False(brief.Chains.ContainsKey("NIFTY"));   // so no buy resolves to an expired contract at Tuesday's prices
        Assert.Contains("NIFTY: no chain recorded yet today (the last capture is from 29 Sep 15:29).", brief.Text);
    }

    [Fact]
    public async Task The_days_own_capture_is_offered()
    {
        var db = NewDb();
        Chain(db, IstTime.FromIst(Tuesday.ToDateTime(new TimeOnly(15, 29))), expiry: Tuesday);
        Chain(db, IstTime.FromIst(Wednesday.ToDateTime(new TimeOnly(9, 16))), expiry: new DateOnly(2026, 10, 6));

        var brief = await Builder(db).BuildAsync(IstTime.FromIst(Wednesday.ToDateTime(new TimeOnly(9, 20))), replay: true, default);

        Assert.Equal(new DateOnly(2026, 10, 6), brief.Chains["NIFTY"].ExpiryDate);
        Assert.Contains("NIFTY expiry 6 Oct", brief.Text);
    }

    private static MarketBriefBuilder Builder(TradingDbContext db)
    {
        var sessions = new MarketSessionService(new MarketReplayTests.OpenCalendar());
        var book = new MarketReplayBook();
        book.Begin(Wednesday);
        // The intelligence and factor sections are not under test: built without them, they say they are not available.
        return new MarketBriefBuilder(db, new LiveDataService(db, new NoProviders(), sessions), new OptionChainService(db, sessions, new Lots(65)),
            null!, null!, sessions, NullLogger<MarketBriefBuilder>.Instance, book);
    }

    internal static void Chain(TradingDbContext db, DateTime captured, DateOnly expiry)
    {
        foreach (var strike in new[] { 22600m, 22650m, 22700m })
        {
            foreach (var type in new[] { "CE", "PE" })
            {
                decimal ltp = type == "CE" ? 22760m - strike : strike - 22540m;
                db.OptionChainSnapshots.Add(new OptionChainSnapshot
                {
                    Underlying = "NIFTY", ExpiryDate = expiry, StrikePrice = strike, OptionType = type,
                    Symbol = $"NSE:NIFTY26{expiry.Month switch { 10 => "O", 11 => "N", 12 => "D", var m => m.ToString() }}{expiry.Day:00}{strike:0}{type}",
                    CapturedUtc = captured, SpotPrice = 22640m, LastTradedPrice = ltp, BidPrice = ltp - 0.5m, AskPrice = ltp + 0.5m, PriceChange = 1m,
                    OpenInterest = 6_000, OpenInterestAtOpen = 5_000, SourceKey = "dhan",
                });
            }
        }

        db.SaveChanges();
    }

    private sealed class NoProviders : IProviderCatalog
    {
        public IReadOnlyList<ProviderDescriptor> Descriptors => [];

        public ProviderDescriptor? Find(string providerKey) => null;
    }

    internal sealed class Lots(int size) : ILotSizeResolver
    {
        public Task<LotSizeInfo> ResolveAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LotSizeInfo(size, LotSizeInfo.SourceConfigured, "NIFTY"));

        public Task<IReadOnlyDictionary<string, LotSizeInfo>> ResolveManyAsync(IEnumerable<string> symbols, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, LotSizeInfo>>(symbols.Distinct().ToDictionary(s => s, s => new LotSizeInfo(size, LotSizeInfo.SourceConfigured, "NIFTY")));

        public Task<LotSizeInfo> ResolveForUnderlyingAsync(string underlying, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LotSizeInfo(size, LotSizeInfo.SourceConfigured, underlying));
    }
}
