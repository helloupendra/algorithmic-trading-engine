using System.Net;
using System.Text;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Providers.Dhan;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Dhan's option chain recorded into the platform's chain history, and the
/// universe the Dhan feed streams beyond the watchlist.
/// </summary>
public class DhanChainPollerTests
{
    private static readonly DateOnly Expiry = new(2026, 9, 15);

    // ------------------------------------------------------------- chain → rows

    [Fact]
    public void A_chain_becomes_snapshot_rows_under_the_platforms_symbols()
    {
        var chain = new DhanOptionChain("NIFTY", Expiry, 24010.5m, new[]
        {
            new DhanChainRow(24000.000000m,
                Side(lastPrice: 120m, previousClose: 100m, oi: 5000, previousOi: 4000, iv: 14.2m, greeks: new DhanGreeks(0.52m, -9.1m, 0.0011m, 12.3m)),
                Side(lastPrice: 95m, previousClose: 110m, oi: 7000, previousOi: null, iv: null, greeks: null)),
            // Listed by Dhan after the platform's instrument download: no symbol yet.
            new DhanChainRow(24050m, Side(lastPrice: 90m), null),
        });
        var contracts = new Dictionary<(decimal, string), string>
        {
            [(24000.00m, "CE")] = "NSE:NIFTY2691524000CE",
            [(24000.00m, "PE")] = "NSE:NIFTY2691524000PE",
        };

        var (rows, unmatched) = DhanChainRows.Build(chain, contracts);

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, unmatched);

        var call = rows.Single(r => r.OptionType == "CE");
        Assert.Equal("NSE:NIFTY2691524000CE", call.Symbol);
        Assert.Equal("NIFTY", call.Underlying);
        Assert.Equal(24010.5m, call.SpotPrice);
        Assert.Equal(20m, call.PriceChange);
        Assert.Equal(4000, call.PreviousDayOpenInterest);
        Assert.Equal(14.2m, call.ImpliedVolatility);
        Assert.Equal(0.52m, call.Delta);
        Assert.Equal("dhan", call.SourceKey);

        var put = rows.Single(r => r.OptionType == "PE");
        Assert.Equal(-15m, put.PriceChange);
        Assert.Null(put.PreviousDayOpenInterest);
        Assert.Null(put.Delta);
    }

    [Fact]
    public void The_nearest_expiry_on_its_own_day_is_todays()
    {
        var expiries = new[] { new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 8) };

        Assert.Equal(new DateOnly(2026, 9, 15), DhanChainRows.NearestExpiry(expiries, new DateOnly(2026, 9, 15)));
        Assert.Equal(new DateOnly(2026, 9, 22), DhanChainRows.NearestExpiry(expiries, new DateOnly(2026, 9, 16)));
        Assert.Null(DhanChainRows.NearestExpiry(expiries, new DateOnly(2026, 9, 23)));
    }

    // ------------------------------------------------------------------ recorder

    [Fact]
    public async Task A_round_stores_the_chain_through_the_option_chain_module()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IstTime.Zone));
        var expiry = today.AddDays(1);
        await using var db = Db();
        db.Instruments.AddRange(
            Option("NSE:NIFTYTESTCE", "NIFTY", expiry, 24000m, "CE"),
            Option("NSE:NIFTYTESTPE", "NIFTY", expiry, 24000m, "PE"));
        await db.SaveChangesAsync();

        var handler = new Routes
        {
            ["/optionchain/expirylist"] = $$"""{"data":["{{expiry:yyyy-MM-dd}}"],"status":"success"}""",
            ["/optionchain"] = """
                {"data":{"last_price":24010.5,"oc":{"24000.000000":{
                  "ce":{"last_price":120,"previous_close_price":100,"oi":5000,"previous_oi":4000,"volume":900,"implied_volatility":14.2,
                        "greeks":{"delta":0.52,"theta":-9.1,"gamma":0.0011,"vega":12.3},"top_bid_price":119.5,"top_ask_price":120.5,"security_id":1},
                  "pe":{"last_price":95,"previous_close_price":110,"oi":7000,"previous_oi":6500,"volume":800,"implied_volatility":13.1,
                        "greeks":{"delta":-0.47,"theta":-8.0,"gamma":0.0011,"vega":12.0},"security_id":2}}}},"status":"success"}
                """,
        };
        var state = new DhanChainPollerState(Options.Create(new DhanSettings()));
        var recorder = Recorder(db, handler, state, marketOpen: true);

        var outcomes = await recorder.RecordAsync(new[] { "NIFTY" }, onlyOpenMarkets: true, CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("recorded", outcome.State);
        Assert.Equal(2, outcome.Rows);
        Assert.Equal(expiry, outcome.Expiry);

        var stored = await db.OptionChainSnapshots.OrderBy(s => s.OptionType).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, s => Assert.Equal("dhan", s.SourceKey));
        Assert.Equal(4000, stored[0].OpenInterestAtOpen);
        Assert.Equal(-0.47m, stored[1].Delta);
        Assert.Same(outcome, state.Outcomes.Single());
    }

    [Fact]
    public async Task An_MCX_chain_is_priced_on_its_future_not_on_the_chains_own_figure()
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IstTime.Zone));
        await using var db = Db();
        db.Instruments.AddRange(
            new Instrument { Symbol = "MCX:CRUDEOILTESTFUT", Exchange = "MCX", Segment = "COM", InstrumentType = "FUT", Underlying = "CRUDEOIL", ExpiryDate = today.AddDays(7), IsEnabled = true },
            new Instrument { Symbol = "MCX:CRUDEOILTEST9950CE", Exchange = "MCX", Segment = "COM", InstrumentType = "CE", OptionType = "CE", Underlying = "CRUDEOIL", ExpiryDate = today.AddDays(3), StrikePrice = 9950m, IsEnabled = true });
        db.InstrumentVendorSymbols.Add(new InstrumentVendorSymbol { ProviderKey = "dhan", CanonicalSymbol = "MCX:CRUDEOILTESTFUT", VendorSymbol = "MCX_COMM:565899:FUTCOM" });
        db.LiveQuotesLatest.Add(new LiveQuoteLatest { Symbol = "MCX:CRUDEOILTESTFUT", LastTradedPrice = 9971m, UpdatedUtc = DateTime.UtcNow, SourceKey = "dhan" });
        await db.SaveChangesAsync();

        var handler = new Routes
        {
            ["/optionchain/expirylist"] = $$"""{"data":["{{today.AddDays(3):yyyy-MM-dd}}"],"status":"success"}""",
            // The figure Dhan sent on 2026-09-14 while the future traded 9,971.
            ["/optionchain"] = """{"data":{"last_price":9577,"oc":{"9950.000000":{"ce":{"last_price":302.3,"oi":1797,"security_id":7}}}},"status":"success"}""",
        };
        var recorder = Recorder(db, handler, new DhanChainPollerState(Options.Create(new DhanSettings())), marketOpen: true);

        var outcome = Assert.Single(await recorder.RecordAsync(new[] { "CRUDEOIL" }, onlyOpenMarkets: true, CancellationToken.None));

        Assert.Equal("recorded", outcome.State);
        Assert.Equal(9971m, outcome.Spot);
        Assert.Equal(9971m, (await db.OptionChainSnapshots.SingleAsync()).SpotPrice);
    }

    [Fact]
    public async Task A_closed_market_is_not_asked_and_says_why()
    {
        await using var db = Db();
        var handler = new Routes();
        var recorder = Recorder(db, handler, new DhanChainPollerState(Options.Create(new DhanSettings())), marketOpen: false);

        var outcome = Assert.Single(await recorder.RecordAsync(new[] { "SENSEX" }, onlyOpenMarkets: true, CancellationToken.None));

        Assert.Equal("idle", outcome.State);
        Assert.Equal("BSE is closed", outcome.Detail);
        Assert.Empty(handler.Called);
    }

    [Fact]
    public async Task A_rejected_token_ends_the_round_instead_of_repeating_for_every_underlying()
    {
        await using var db = Db();
        var handler = new Routes(HttpStatusCode.Unauthorized)
        {
            ["/optionchain/expirylist"] = """{"errorType":"Invalid_Authentication","errorCode":"DH-901","errorMessage":"Client ID or user generated access token is invalid or expired."}""",
        };
        var recorder = Recorder(db, handler, new DhanChainPollerState(Options.Create(new DhanSettings())), marketOpen: true);

        var outcomes = await recorder.RecordAsync(new[] { "NIFTY", "BANKNIFTY" }, onlyOpenMarkets: true, CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("auth-failed", outcome.State);
    }

    [Fact]
    public void Configured_lists_are_replaced_not_appended_to()
    {
        var settings = new DhanChainPollerSettings { Underlyings = " nifty, CRUDEOIL ,,NIFTY" };
        Assert.Equal(new[] { "NIFTY", "CRUDEOIL" }, settings.UnderlyingList);
    }

    // ------------------------------------------------------------------ universe

    [Fact]
    public void At_the_money_strikes_are_counted_in_listed_strikes()
    {
        var ladder = Enumerable.Range(0, 41).Select(i => 23000m + i * 50m).Reverse();

        var picked = DhanUniverseRules.NearestStrikes(ladder, 24013m, eachSide: 2);

        Assert.Equal(new[] { 23900m, 23950m, 24000m, 24050m, 24100m }, picked);
        // At the edge of the ladder the window is cut, not shifted.
        Assert.Equal(new[] { 23000m, 23050m }, DhanUniverseRules.NearestStrikes(ladder, 22000m, eachSide: 1));
        Assert.Empty(DhanUniverseRules.NearestStrikes(ladder, 0m, eachSide: 2));
    }

    [Fact]
    public void On_expiry_day_the_next_expiry_is_streamed_too()
    {
        var expiries = new[] { new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 22), new DateOnly(2026, 9, 29) };

        Assert.Equal(new[] { new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 22) },
            DhanUniverseRules.OptionExpiries(expiries, new DateOnly(2026, 9, 15)));
        Assert.Equal(new[] { new DateOnly(2026, 9, 22) },
            DhanUniverseRules.OptionExpiries(expiries, new DateOnly(2026, 9, 16)));
    }

    [Fact]
    public void Last_prices_are_read_by_segment_and_security_id()
    {
        // The answer Dhan gave on 2026-09-14, an NSE holiday: nothing for indices.
        using var doc = JsonDocument.Parse("""
            {"data":{"IDX_I":{},"MCX_COMM":{"565899":{"last_price":10007},"568245":{"last_price":277.3}}},"status":"success"}
            """);

        var prices = DhanUniverseBuilder.ReadLastPrices(doc.RootElement);

        Assert.Equal(2, prices.Count);
        Assert.Equal(10007m, prices[("MCX_COMM", 565899)]);
        Assert.Equal(277.3m, prices[("MCX_COMM", 568245)]);
    }

    // ------------------------------------------------------------------ fixtures

    private static DhanChainSide Side(
        decimal? lastPrice = null, decimal? previousClose = null, long? oi = null, long? previousOi = null,
        decimal? iv = null, DhanGreeks? greeks = null) =>
        new(0, lastPrice, previousClose, null, null, null, null, null, oi, previousOi, null, null, iv, greeks);

    private static Instrument Option(string symbol, string underlying, DateOnly expiry, decimal strike, string type) => new()
    {
        Symbol = symbol,
        Exchange = "NSE",
        Segment = "FO",
        InstrumentType = type,
        OptionType = type,
        Underlying = underlying,
        ExpiryDate = expiry,
        StrikePrice = strike,
        IsEnabled = true,
    };

    private static TradingDbContext Db() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"dhan-chain-{Guid.NewGuid():N}").Options);

    private static DhanChainRecorder Recorder(TradingDbContext db, Routes handler, DhanChainPollerState state, bool marketOpen)
    {
        var api = new DhanApiClient(
            Options.Create(new DhanSettings { AccessToken = "token" }),
            new Credentials(),
            new NoSessions(),
            new Factory(handler),
            new DhanRateGate(),
            NullLogger<DhanApiClient>.Instance);
        var chain = new DhanOptionChainClient(api, NullLogger<DhanOptionChainClient>.Instance);
        return new DhanChainRecorder(chain, api, new OptionChainService(db), db, new Sessions(marketOpen), state, NullLogger<DhanChainRecorder>.Instance);
    }

    /// <summary>Answers by path, longest match first, so "/optionchain" never shadows its expiry list.</summary>
    private sealed class Routes(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler, IEnumerable<KeyValuePair<string, string>>
    {
        private readonly Dictionary<string, string> _bodies = new();
        public List<string> Called { get; } = new();

        public string this[string path] { set => _bodies[path] = value; }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _bodies.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Called.Add(path);
            var match = _bodies.Keys.Where(k => path.EndsWith(k, StringComparison.Ordinal)).OrderByDescending(k => k.Length).FirstOrDefault();
            return Task.FromResult(match is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(status) { Content = new StringContent(_bodies[match], Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Credentials : IBrokerCredentialsProvider
    {
        public Task<BrokerCredentials> GetAsync(string providerKey, long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new BrokerCredentials("1100000001", string.Empty, string.Empty, null, "test", null, null));

        public Task SaveAsync(string providerKey, string clientId, string secretKey, string redirectUri, string updatedBy,
            string? tradingPin = null, long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoSessions : IBrokerSessionStore
    {
        public Task<BrokerSession?> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult<BrokerSession?>(null);
        public Task<BrokerSession?> GetForAccountAsync(long brokerAccountId, CancellationToken cancellationToken = default) => Task.FromResult<BrokerSession?>(null);
        public Task ClearAccountAsync(long brokerAccountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BrokerSession?> GetForProviderAsync(string providerKey, CancellationToken cancellationToken = default) => Task.FromResult<BrokerSession?>(null);
        public Task SaveAsync(BrokerSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(string? providerKey = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Sessions(bool open) : IMarketSessionService
    {
        public MarketSessionInfo GetSessionInfo(DateTime utcNow, string exchange, string segment) => throw new NotSupportedException();
        public bool IsMarketOpen(DateTime utcNow, string exchange, string segment) => open;
        public DateTime GetNextMarketOpenUtc(DateTime utcNow, string exchange, string segment) => throw new NotSupportedException();
    }
}
