using System.Net;
using System.Text;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Providers.TrueData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The TrueData history adapter, driven by the bytes the vendor actually sent.
///
/// The sample below is the real answer to
/// GET history.truedata.in/getbars?symbol=NIFTY 50&amp;interval=5min on
/// 2026-09-11, trimmed to four bars. Its shape is the thing worth pinning: a
/// bare array per bar, the timestamp in IST with no offset on it, and open
/// interest in the last slot.
/// </summary>
public class TrueDataHistoryTests
{
    private const string RealBody =
        """
        {"status":"Success","Records":[
        ["2026-09-10T09:15:00",23446.6,23494.95,23431.35,23460.3,0,0],
        ["2026-09-10T09:20:00",23460.3,23463.2,23414.0,23428.6,125,0],
        ["2026-09-11T15:25:00",23398.1,23398.1,23398.1,23398.1,0,4210],
        ["broken row"]]}
        """;

    [Fact]
    public async Task Bars_are_parsed_with_IST_timestamps_turned_into_UTC()
    {
        var (provider, _) = Build(RealBody);

        var bars = await provider.GetHistoryAsync("NSE:NIFTY50-INDEX", "5", new DateTime(2026, 9, 10), new DateTime(2026, 9, 12));

        // The broken row is dropped, the three real ones survive.
        Assert.Equal(3, bars.Count);

        // 09:15 IST is 03:45 UTC. Storing the IST clock as UTC would put every
        // bar of the day five and a half hours early.
        Assert.Equal(new DateTime(2026, 9, 10, 3, 45, 0, DateTimeKind.Utc), bars[0].TimestampUtc);
        Assert.Equal(23446.6m, bars[0].Open);
        Assert.Equal(23494.95m, bars[0].High);
        Assert.Equal(23431.35m, bars[0].Low);
        Assert.Equal(23460.3m, bars[0].Close);
        Assert.Equal(125m, bars[1].Volume);
    }

    [Fact]
    public async Task Zero_open_interest_is_reported_as_unknown_not_as_zero()
    {
        var (provider, _) = Build(RealBody);

        var bars = await provider.GetHistoryAsync("NSE:NIFTY50-INDEX", "5", new DateTime(2026, 9, 10), new DateTime(2026, 9, 12));

        // An index carries no OI and the feed sends 0. Recording that as a real
        // zero would read as "every position was closed".
        Assert.Null(bars[0].OpenInterest);
        Assert.Equal(4210, bars[2].OpenInterest);
    }

    [Fact]
    public async Task The_canonical_symbol_is_translated_before_it_is_sent()
    {
        var (provider, handler) = Build(RealBody);

        await provider.GetHistoryAsync("NSE:NIFTY50-INDEX", "5", new DateTime(2026, 9, 10), new DateTime(2026, 9, 12));

        // "NIFTY 50" is TrueData's name for the index, percent-encoded as the
        // vendor's own examples show it.
        Assert.Contains("symbol=NIFTY%2050", handler.LastUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("interval=5min", handler.LastUrl);
        // The window is asked for in IST, because that is the clock the answers
        // come back on. Midnight UTC is 05:30 that morning in Mumbai.
        Assert.Contains("from=260910T05%3A30%3A00", handler.LastUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_symbol_with_no_known_vendor_name_is_rejected_not_guessed()
    {
        var (provider, _) = Build(RealBody);

        // A monthly option: the canonical name has a month and no expiry day,
        // and no mapping row exists in this test.
        var ex = await Assert.ThrowsAsync<ProviderSymbolRejectedException>(
            () => provider.GetHistoryAsync("NSE:BANKNIFTY26SEP57500CE", "5", new DateTime(2026, 9, 10), new DateTime(2026, 9, 12)));

        Assert.Equal("truedata", ex.ProviderKey);
        Assert.Contains("symbol master", ex.Reason);
    }

    [Fact]
    public async Task A_status_that_is_not_success_is_one_symbols_problem()
    {
        // TrueData answers an unlisted symbol with HTTP 200 and a status. The
        // caller must be able to skip that symbol and keep the sweep going.
        var (provider, _) = Build("""{"status":"No data","Records":[]}""");

        await Assert.ThrowsAsync<ProviderSymbolRejectedException>(
            () => provider.GetHistoryAsync("NSE:NIFTY50-INDEX", "5", new DateTime(2026, 9, 10), new DateTime(2026, 9, 12)));
    }

    [Fact]
    public async Task A_rejected_token_is_renewed_once_and_the_call_retried()
    {
        var (provider, handler) = Build(RealBody, firstResponseStatus: HttpStatusCode.Unauthorized);

        var bars = await provider.GetHistoryAsync("NSE:NIFTY50-INDEX", "5", new DateTime(2026, 9, 10), new DateTime(2026, 9, 12));

        Assert.Equal(3, bars.Count);
        // One 401, one retry: the adapter fixed its own token rather than
        // failing a backfill and leaving the gap it was run to fill.
        Assert.Equal(2, handler.HistoryCalls);
        Assert.Equal(2, handler.TokenCalls);
    }

    // ---------------------------------------------------------------- fixtures

    private static (TrueDataMarketDataProvider Provider, FakeHandler Handler) Build(
        string historyBody,
        HttpStatusCode? firstResponseStatus = null)
    {
        var handler = new FakeHandler(historyBody, firstResponseStatus);
        var factory = new FakeHttpClientFactory(handler);
        var settings = Options.Create(new TrueDataSettings());

        var tokens = new TrueDataTokenStore(
            settings, new FakeCredentials(), factory, NullLogger<TrueDataTokenStore>.Instance);

        var provider = new TrueDataMarketDataProvider(
            settings, tokens, new IdentitySymbolMapper(), factory,
            NullLogger<TrueDataMarketDataProvider>.Instance);

        return (provider, handler);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _historyBody;
        private readonly HttpStatusCode? _firstStatus;

        public FakeHandler(string historyBody, HttpStatusCode? firstStatus)
        {
            _historyBody = historyBody;
            _firstStatus = firstStatus;
        }

        public string LastUrl { get; private set; } = string.Empty;
        public int HistoryCalls { get; private set; }
        public int TokenCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.OriginalString;

            if (url.Contains("/token", StringComparison.OrdinalIgnoreCase))
            {
                TokenCalls++;
                return Task.FromResult(Json(HttpStatusCode.OK,
                    $$"""{"access_token":"tok-{{TokenCalls}}","token_type":"bearer","expires_in":42915}"""));
            }

            HistoryCalls++;
            LastUrl = request.RequestUri!.OriginalString;

            if (HistoryCalls == 1 && _firstStatus is { } status)
                return Task.FromResult(Json(status, """{"message":"token expired"}"""));

            return Task.FromResult(Json(HttpStatusCode.OK, _historyBody));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class FakeCredentials : IBrokerCredentialsProvider
    {
        public Task<BrokerCredentials> GetAsync(string providerKey, long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new BrokerCredentials("user", "secret", string.Empty, null, "test", null, null));

        public Task SaveAsync(string providerKey, string clientId, string secretKey, string redirectUri, string updatedBy,
            string? tradingPin = null, long? brokerAccountId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>No mapping rows, so the grammar rules decide — as on a fresh install.</summary>
    private sealed class IdentitySymbolMapper : ISymbolMapper
    {
        public Task<string> ToVendorAsync(string canonicalSymbol, string providerKey, CancellationToken cancellationToken = default)
            => Task.FromResult(canonicalSymbol);

        public Task<string> FromVendorAsync(string vendorSymbol, string providerKey, CancellationToken cancellationToken = default)
            => Task.FromResult(vendorSymbol);

        public Task<IReadOnlyDictionary<string, string>> ToVendorManyAsync(IReadOnlyCollection<string> canonicalSymbols, string providerKey, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(canonicalSymbols.ToDictionary(x => x, x => x));

        public Task MapAsync(string canonicalSymbol, string providerKey, string vendorSymbol, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
