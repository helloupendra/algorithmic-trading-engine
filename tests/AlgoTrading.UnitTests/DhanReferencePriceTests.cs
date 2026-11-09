using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using AlgoTrading.Infrastructure.Providers.Dhan;
using Xunit;

namespace AlgoTrading.UnitTests;

public class DhanReferencePriceTests
{
    [Theory]
    [InlineData("{\"last_price\":8224,\"net_change\":-330,\"ohlc\":{\"close\":9999}}", 8554)]
    [InlineData("{\"last_price\":143850,\"net_change\":1298}", 142552)]
    [InlineData("{\"last_price\":100,\"net_change\":0}", 100)]
    public void Uses_explicit_net_change_not_the_day_close(string json, decimal expected)
        => Assert.Equal(expected, DhanReferencePrices.ReadPreviousClose(JsonDocument.Parse(json).RootElement));

    [Theory]
    [InlineData("{\"last_price\":100,\"ohlc\":{\"close\":100}}")]
    [InlineData("{\"last_price\":100,\"net_change\":null}")]
    [InlineData("{\"last_price\":0,\"net_change\":-100}")]
    [InlineData("{\"last_price\":100,\"net_change\":200}")]
    public void Missing_or_invalid_reference_stays_unknown(string json)
        => Assert.Null(DhanReferencePrices.ReadPreviousClose(JsonDocument.Parse(json).RootElement));

    [Fact]
    public void Cached_reference_expires_and_never_crosses_an_IST_date_or_stale_quote()
    {
        var cache = new DhanReferencePrices();
        var now = new DateTime(2026, 11, 9, 18, 29, 0, DateTimeKind.Utc);
        cache.Replace(now, new Dictionary<string, decimal> { ["NIFTY"] = 24986.4m });
        Assert.Equal(24986.4m, cache.PreviousClose("NIFTY", now, now));
        Assert.Null(cache.PreviousClose("NIFTY", now.AddDays(-1), now));
        Assert.Null(cache.PreviousClose("NIFTY", now, now.AddMinutes(1)));
        cache.Replace(now.AddHours(-1), new Dictionary<string, decimal> { ["NIFTY"] = 24986.4m });
        Assert.Null(cache.PreviousClose("NIFTY", now, now));
    }

    [Theory]
    [InlineData("dhan", null, 90d, 10d, 11.11)]
    [InlineData("dhan", 80d, 80d, 20d, 25d)]
    [InlineData("fyers", null, null, null, null)]
    public async Task Overview_uses_reference_only_for_missing_Dhan_closes(string provider, double? feedClose, double? previous, double? change, double? percent)
    {
        using var desk = new RunnerDesk();
        using var db = desk.Db();
        var now = DateTime.UtcNow;
        db.LiveQuotesLatest.Add(new LiveQuoteLatest { Symbol = "NSE:NIFTY50-INDEX", SourceKey = provider,
            LastTradedPrice = 100, Close = (decimal?)feedClose, UpdatedUtc = now, ExchangeTimestampUtc = now });
        db.SaveChanges();
        var cache = new DhanReferencePrices();
        cache.Replace(now, new Dictionary<string, decimal> { ["NSE:NIFTY50-INDEX"] = 90 });
        var service = new MarketPulseService(db, RecapClockTests.Inert<ILiveDataService>.Create(),
            RecapClockTests.Inert<IRedisPublisherService>.Create(), NullLogger<MarketPulseService>.Instance, cache);
        var item = (await service.GetAsync()).Groups.Single(g => g.Key == "index").Items.First();
        Assert.Equal((decimal?)previous, item.PreviousClose);
        Assert.Equal((decimal?)change, item.Change);
        Assert.Equal((decimal?)percent, item.ChangePercent);
        Assert.Equal((decimal?)feedClose, db.LiveQuotesLatest.Single().Close); // executable quotes are not overwritten
    }

    [Fact]
    public void Daily_reference_is_reused_only_for_the_requested_exchange_session()
    {
        var cache = new DhanReferencePrices();
        var day = new DateOnly(2026, 11, 8);
        cache.RememberDailyClose("NIFTY", day, 24986.4m);
        Assert.Equal(24986.4m, cache.DailyClose("NIFTY", day));
        Assert.Null(cache.DailyClose("NIFTY", day.AddDays(1)));
    }
}
