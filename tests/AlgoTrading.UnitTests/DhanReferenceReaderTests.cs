using System.Net;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.ValueObjects;
using AlgoTrading.Infrastructure.Providers.Dhan;
using AlgoTrading.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AlgoTrading.UnitTests;

public class DhanReferenceReaderTests
{
    private const string Symbol = "NSE:NIFTY50-INDEX";
    private static readonly DateTime Now = new(2026,11,9,15,0,0,DateTimeKind.Utc);

    [Theory]
    [InlineData(false, 24986.4, 1)]
    [InlineData(true, 25044.65, 0)]
    public async Task Closed_market_reset_uses_the_preceding_special_session_while_live_zero_is_valid(bool open, decimal expected, int historyCalls)
    {
        var transport = new Transport();
        var gate = new DhanRateGate(Enum.GetValues<DhanRateClass>().ToDictionary(c => c, _ => TimeSpan.Zero));
        var api = new DhanApiClient(Options.Create(new DhanSettings { AccessToken = "test-reference-token" }), new Credentials(),
            RecapClockTests.Inert<IBrokerSessionStore>.Create(), new Factory(transport), gate, NullLogger<DhanApiClient>.Instance);
        var cache = new DhanReferencePrices();
        var reader = new DhanReferencePriceReader(api, new Mapper(), cache, new Calendar(open), new Clock());
        await reader.RefreshAsync([Symbol], CancellationToken.None);
        Assert.Equal(expected, cache.PreviousClose(Symbol, Now, Now));
        await reader.RefreshAsync([Symbol], CancellationToken.None);
        Assert.Equal(historyCalls, transport.HistoryCalls); // daily reference is reused, not fetched every minute
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Calendar(bool open) : IMarketSessionService
    {
        public bool IsMarketOpen(DateTime utcNow, string exchange, string segment) => open;
        public MarketSessionInfo GetSessionInfo(DateTime utcNow, string exchange, string segment) => new()
        { IsTradingDay = IstTime.DateOf(utcNow) == new DateOnly(2026,11,8) };
        public DateTime GetNextMarketOpenUtc(DateTime utcNow, string exchange, string segment) => throw new NotSupportedException();
    }
    private sealed class Credentials : IBrokerCredentialsProvider
    {
        public Task<BrokerCredentials> GetAsync(string providerKey, long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new BrokerCredentials("test-client", "", "", null, "test", null, null));
        public Task SaveAsync(string providerKey, string clientId, string secretKey, string redirectUri, string updatedBy, string? tradingPin = null, long? brokerAccountId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Mapper : ISymbolMapper
    {
        public Task<IReadOnlyDictionary<string,string>> ToVendorManyAsync(IReadOnlyCollection<string> canonicalSymbols, string providerKey, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string,string>>(new Dictionary<string,string>());
        public Task<string> ToVendorAsync(string canonicalSymbol, string providerKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> FromVendorAsync(string vendorSymbol, string providerKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task MapAsync(string canonicalSymbol, string providerKey, string vendorSymbol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler:false); }
    private sealed class Transport : HttpMessageHandler
    {
        public int HistoryCalls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body;
            if (request.RequestUri!.AbsolutePath.EndsWith("/marketfeed/quote"))
                body = """{"data":{"IDX_I":{"13":{"last_price":25044.65,"net_change":0,"ohlc":{"close":25044.65}}}}}""";
            else
            {
                HistoryCalls++;
                using var query = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("2026-11-08", query.RootElement.GetProperty("fromDate").GetString());
                var stamp = new DateTimeOffset(IstTime.StartOfDayUtc(new DateOnly(2026,11,8))).ToUnixTimeSeconds();
                body = $$"""{"timestamp":[{{stamp}}],"open":[25000],"high":[25010],"low":[24900],"close":[24986.4],"volume":[1]}""";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
