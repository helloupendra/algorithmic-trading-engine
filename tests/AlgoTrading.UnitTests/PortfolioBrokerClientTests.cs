using System.Net;
using System.Text.Json;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Infrastructure.Portfolio;
using AlgoTrading.Infrastructure.Providers.Angel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using static AlgoTrading.UnitTests.PortfolioTestKit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Each broker's read-only answers, read from recorded shapes: holdings, positions and funds in the desk's own form,
/// the headers each broker wants, and a refused token told apart from a broker that is merely down.
/// </summary>
public class PortfolioBrokerClientTests
{
    // ---------- FYERS ----------

    [Fact]
    public void Fyers_holdings_are_read_with_the_quantity_left_after_today_s_sales()
    {
        using var doc = JsonDocument.Parse(Fixture("fyers_holdings.json"));

        var holdings = FyersPortfolioClient.Holdings(doc.RootElement);

        // ITC was sold in full today (remainingQuantity 0): it is no longer held.
        Assert.Equal(["NSE:RELIANCE-EQ", "NSE:TCS-EQ", "NSE:INFY-EQ"], holdings.Select(h => h.Symbol));
        var reliance = holdings[0];
        Assert.Equal(("NSE", "INE002A01018", 20m, 2450m, 2520m), (reliance.Exchange, reliance.Isin, reliance.Quantity, reliance.AvgPrice, reliance.Ltp));
        Assert.Equal(PriceSources.Broker, reliance.PriceSource);
    }

    [Fact]
    public void Fyers_positions_tell_a_derivative_by_its_segment_and_keep_a_position_closed_today()
    {
        using var doc = JsonDocument.Parse(Fixture("fyers_positions.json"));

        var positions = FyersPortfolioClient.Positions(doc.RootElement);

        Assert.Equal(2, positions.Count);
        var option = positions[0];
        Assert.Equal(("NSE:NIFTY26OCT25000CE", "MARGIN", 75m, 120.5m, 130m, 712.5m, true),
            (option.Symbol, option.Product, option.Quantity, option.AvgPrice, option.Ltp, option.Pnl, option.IsDerivative));
        Assert.Equal((0m, 50m, false), (positions[1].Quantity, positions[1].Pnl, positions[1].IsDerivative));
    }

    [Fact]
    public void Fyers_funds_are_found_by_title_with_equity_and_commodity_together()
    {
        using var doc = JsonDocument.Parse(Fixture("fyers_funds.json"));

        Assert.Equal(new BrokerFunds(250962.5m, 9037.5m, 260000m), FyersPortfolioClient.Funds(doc.RootElement));
    }

    [Fact]
    public async Task Fyers_reads_with_the_app_id_and_token_header_and_only_get_requests()
    {
        var broker = new FakeBroker()
            .Get(FyersPortfolioClient.HoldingsPath, Fixture("fyers_holdings.json"))
            .Get(FyersPortfolioClient.PositionsPath, Fixture("fyers_positions.json"))
            .Get(FyersPortfolioClient.FundsPath, Fixture("fyers_funds.json"));
        var client = Fyers(broker);

        var read = await client.ReadAsync(new PortfolioToken("tok-123", AppId: "APP-100"), CancellationToken.None);

        Assert.Equal(3, read.Holdings.Count);
        Assert.Equal(3, broker.Requests.Count);
        Assert.All(broker.Requests, r =>
        {
            Assert.Equal("GET", r.Method);
            Assert.Equal("APP-100:tok-123", r.Headers["Authorization"]);
        });
    }

    [Fact]
    public async Task Fyers_exchanges_the_code_with_the_hash_of_app_id_and_secret_and_never_sends_the_secret()
    {
        var broker = new FakeBroker().On("POST", FyersPortfolioClient.TokenPath, HttpStatusCode.OK,
            """{"s":"ok","code":200,"message":"","access_token":"issued-token"}""");
        var client = Fyers(broker, new PortfolioSettings { Fyers = { AppId = "APP-100", SecretKey = "shh-secret", RedirectUri = "https://openfno.test/api/Portfolio/callback/fyers" } });

        var token = await client.ExchangeAsync("the-auth-code", CancellationToken.None);

        Assert.Equal(new PortfolioToken("issued-token", AppId: "APP-100"), token);
        var sent = JsonDocument.Parse(broker.Requests.Single().Body!).RootElement;
        Assert.Equal("authorization_code", sent.GetProperty("grant_type").GetString());
        Assert.Equal("the-auth-code", sent.GetProperty("code").GetString());
        Assert.Equal(FyersPortfolioClient.AppIdHash("APP-100", "shh-secret"), sent.GetProperty("appIdHash").GetString());
        Assert.Equal(64, sent.GetProperty("appIdHash").GetString()!.Length);
        Assert.DoesNotContain("shh-secret", broker.Requests.Single().Body!);
    }

    [Fact]
    public async Task Without_its_own_app_fyers_uses_the_platform_s_app_and_its_registered_redirect()
    {
        var platform = new BrokerCredentials("PLAT-100", "plat-secret", "https://openfno.test/api/Auth/callback", null, "database", "admin", null);
        var client = Fyers(new FakeBroker(), credentials: new Credentials(platform));

        var app = await client.AppAsync(CancellationToken.None);
        string url = FyersPortfolioClient.AuthorizeUrl(app!, "pf-abc");

        Assert.Equal("platform", app!.Source);
        Assert.StartsWith("https://api-t1.fyers.in/api/v3/generate-authcode?client_id=PLAT-100", url);
        Assert.Contains("redirect_uri=https%3A%2F%2Fopenfno.test%2Fapi%2FAuth%2Fcallback", url);
        Assert.Contains("&state=pf-abc", url);
        Assert.DoesNotContain("plat-secret", url);
    }

    [Theory]
    [InlineData(200, """{"s":"error","code":-16,"message":"Could not authenticate the user"}""", true)]
    [InlineData(401, """{"s":"error","code":-15,"message":"token expired"}""", true)]
    [InlineData(503, """<html>busy</html>""", false)]
    [InlineData(200, """{"s":"error","code":-300,"message":"Something went wrong"}""", false)]
    public async Task Fyers_tells_a_refused_token_from_a_broker_that_is_down(int status, string body, bool rejected)
    {
        var broker = new FakeBroker().On("GET", FyersPortfolioClient.ProfilePath, (HttpStatusCode)status, body);

        var ex = await Assert.ThrowsAsync<PortfolioBrokerException>(() =>
            Fyers(broker).AccountIdAsync(new PortfolioToken("secret-token-value", AppId: "APP-100"), CancellationToken.None));

        Assert.Equal(rejected, ex.TokenRejected);
        Assert.DoesNotContain("secret-token-value", ex.Message);
    }

    [Fact]
    public void Fyers_profile_gives_only_the_account_id()
    {
        using var doc = JsonDocument.Parse(Fixture("fyers_profile.json"));

        Assert.Equal("XA00071", FyersPortfolioClient.ProfileId(doc.RootElement));
    }

    // ---------- Dhan ----------

    [Fact]
    public void Dhan_holdings_carry_no_price_and_a_holding_on_both_exchanges_names_neither()
    {
        using var doc = JsonDocument.Parse(Fixture("dhan_holdings.json"));

        var holdings = DhanPortfolioClient.Holdings(doc.RootElement);

        Assert.Equal(2, holdings.Count);
        Assert.Equal(("RELIANCE", "", "INE002A01018", 10m, 2550m), (holdings[0].Symbol, holdings[0].Exchange, holdings[0].Isin, holdings[0].Quantity, holdings[0].AvgPrice));
        Assert.Equal(("NSE", 25m), (holdings[1].Exchange, holdings[1].Quantity));
        Assert.All(holdings, h => Assert.Null(h.Ltp));
    }

    [Fact]
    public void Dhan_positions_add_realised_and_unrealised_and_name_the_fno_segment()
    {
        using var doc = JsonDocument.Parse(Fixture("dhan_positions.json"));

        var p = Assert.Single(DhanPortfolioClient.Positions(doc.RootElement));

        Assert.Equal(("BANKNIFTY-Oct2026-FUT", "NSE_FNO", "MARGIN", -30m, 52000m, -1500m, true),
            (p.Symbol, p.Exchange, p.Product, p.Quantity, p.AvgPrice, p.Pnl, p.IsDerivative));
        Assert.Null(p.Ltp);
    }

    [Fact]
    public void Dhan_fund_limit_is_read_with_dhan_s_own_spelling()
    {
        using var doc = JsonDocument.Parse(Fixture("dhan_fundlimit.json"));

        Assert.Equal(new BrokerFunds(45000.5m, 14999.5m, 60000m), DhanPortfolioClient.Funds(doc.RootElement));
    }

    [Fact]
    public async Task Dhan_sends_the_pasted_token_and_client_id_as_headers()
    {
        var broker = new FakeBroker().Get(DhanPortfolioClient.ProfilePath, Fixture("dhan_profile.json"));

        string id = await new DhanPortfolioClient(new Factory(broker))
            .AccountIdAsync(new PortfolioToken("pasted-token", ClientId: "1100000042"), CancellationToken.None);

        Assert.Equal("1100000042", id);
        var seen = broker.Requests.Single();
        Assert.Equal(("pasted-token", "1100000042"), (seen.Headers["access-token"], seen.Headers["client-id"]));
    }

    [Fact]
    public async Task Dhan_answers_an_account_with_no_holdings_as_empty_but_any_other_data_error_as_a_failure()
    {
        var broker = new FakeBroker()
            .On("GET", DhanPortfolioClient.HoldingsPath, HttpStatusCode.BadRequest, """{"errorType":"Data_Error","errorCode":"DH-907","errorMessage":"No holdings available"}""")
            .On("GET", DhanPortfolioClient.PositionsPath, HttpStatusCode.OK, "[]")
            .Get(DhanPortfolioClient.FundsPath, Fixture("dhan_fundlimit.json"));
        var client = new DhanPortfolioClient(new Factory(broker));

        var read = await client.ReadAsync(new PortfolioToken("t", ClientId: "1100000042"), CancellationToken.None);
        Assert.Empty(read.Holdings);

        var failing = new FakeBroker()
            .On("GET", DhanPortfolioClient.HoldingsPath, HttpStatusCode.BadRequest, """{"errorType":"Data_Error","errorCode":"DH-907","errorMessage":"Incorrect parameters"}""");
        var ex = await Assert.ThrowsAsync<PortfolioBrokerException>(() =>
            new DhanPortfolioClient(new Factory(failing)).ReadAsync(new PortfolioToken("t"), CancellationToken.None));
        Assert.False(ex.TokenRejected);
        Assert.Contains("DH-907", ex.Message);
    }

    [Fact]
    public async Task Dhan_dh_901_is_a_token_to_generate_again()
    {
        var broker = new FakeBroker().On("GET", DhanPortfolioClient.ProfilePath, HttpStatusCode.Unauthorized,
            """{"errorType":"Invalid_Authentication","errorCode":"DH-901","errorMessage":"Client ID or user generated access token is invalid or expired."}""");

        var ex = await Assert.ThrowsAsync<PortfolioBrokerException>(() =>
            new DhanPortfolioClient(new Factory(broker)).AccountIdAsync(new PortfolioToken("old"), CancellationToken.None));

        Assert.True(ex.TokenRejected);
        Assert.Contains("web.dhan.co", ex.Message);
    }

    [Fact]
    public void A_dhan_token_ends_when_its_own_claim_says_and_else_24_hours_after_it_was_pasted()
    {
        var client = new DhanPortfolioClient(new Factory(new FakeBroker()));
        var issued = new DateTime(2026, 10, 18, 4, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 10, 19, 3, 30, 0, DateTimeKind.Utc);

        Assert.Equal(end, client.ExpiresUtc(new PortfolioToken(Jwt(end)), issued));
        Assert.Equal(issued.AddHours(24), client.ExpiresUtc(new PortfolioToken("not-a-jwt"), issued));
    }

    // ---------- Angel One ----------

    [Fact]
    public void Angel_holdings_read_numbers_sent_as_numbers_or_as_strings()
    {
        using var doc = JsonDocument.Parse(Fixture("angel_holdings.json"));

        var holdings = AngelPortfolioClient.Holdings(doc.RootElement);

        Assert.Equal(2, holdings.Count);
        Assert.Equal(("TCS-EQ", "NSE", "INE467B01029", 5m, 3500m, 3550m), (holdings[0].Symbol, holdings[0].Exchange, holdings[0].Isin, holdings[0].Quantity, holdings[0].AvgPrice, holdings[0].Ltp));
        Assert.Equal((100m, 395.5m, 410m), (holdings[1].Quantity, holdings[1].AvgPrice, holdings[1].Ltp));
    }

    [Fact]
    public void Angel_positions_are_strings_and_an_index_option_is_a_derivative()
    {
        using var doc = JsonDocument.Parse(Fixture("angel_positions.json"));

        var p = Assert.Single(AngelPortfolioClient.Positions(doc.RootElement));

        Assert.Equal(("NIFTY27OCT2625200PE", "NFO", "CARRYFORWARD", -75m, 90m, 80m, 750m, true),
            (p.Symbol, p.Exchange, p.Product, p.Quantity, p.AvgPrice, p.Ltp, p.Pnl, p.IsDerivative));
    }

    [Fact]
    public void Angel_with_no_positions_answers_null_data_and_that_is_none()
    {
        using var doc = JsonDocument.Parse("""{"status":true,"message":"SUCCESS","errorcode":"","data":null}""");

        Assert.Empty(AngelPortfolioClient.Positions(doc.RootElement));
    }

    [Fact]
    public void Angel_rms_limits_become_available_used_and_total()
    {
        using var doc = JsonDocument.Parse(Fixture("angel_rms.json"));

        Assert.Equal(new BrokerFunds(125000.5m, 5400.25m, 125000.5m), AngelPortfolioClient.Funds(doc.RootElement));
    }

    [Fact]
    public async Task Angel_reads_with_the_session_jwt_and_the_app_key()
    {
        var broker = new FakeBroker().Get(AngelPortfolioClient.ProfilePath, Fixture("angel_profile.json"));
        var client = Angel(broker);

        string id = await client.AccountIdAsync(client.FromRedirect("Bearer session-jwt"), CancellationToken.None);

        Assert.Equal("T1234567", id);
        var seen = broker.Requests.Single();
        Assert.Equal("GET", seen.Method);
        Assert.Equal("Bearer session-jwt", seen.Headers["Authorization"]);
        Assert.Equal("angel-key", seen.Headers["X-PrivateKey"]);
        Assert.Equal("USER", seen.Headers["X-UserType"]);
    }

    [Theory]
    [InlineData(200, """{"status":false,"message":"Invalid Token","errorcode":"AG8001","data":null}""", true)]
    [InlineData(200, """{"status":false,"message":"Something went wrong","errorcode":"AB1004","data":null}""", false)]
    [InlineData(403, "Access denied because of exceeding access rate", false)]
    public async Task Angel_tells_a_refused_session_from_a_refusal_for_another_reason(int status, string body, bool rejected)
    {
        var broker = new FakeBroker().On("GET", AngelPortfolioClient.HoldingsPath, (HttpStatusCode)status, body);

        var ex = await Assert.ThrowsAsync<PortfolioBrokerException>(() =>
            Angel(broker).ReadAsync(new PortfolioToken("session-jwt", AppId: "angel-key"), CancellationToken.None));

        Assert.Equal(rejected, ex.TokenRejected);
        Assert.DoesNotContain("session-jwt", ex.Message);
    }

    [Fact]
    public void The_angel_login_url_carries_the_app_key_and_the_state()
    {
        Assert.Equal("https://smartapi.angelone.in/publisher-login?api_key=angel-key&state=pf-123",
            AngelPortfolioClient.LoginUrl("angel-key", "pf-123"));
    }

    // ---------- the vault ----------

    [Fact]
    public void A_stored_token_is_unreadable_text_that_only_the_vault_reads_back()
    {
        var keys = new EphemeralDataProtectionProvider();
        var vault = new PortfolioTokenVault(keys);
        var token = new PortfolioToken("the-access-token", AppId: "APP-100", ClientId: "XA00071");

        string stored = vault.Protect(token);

        Assert.DoesNotContain("the-access-token", stored);
        Assert.Equal(token, vault.Unprotect(stored));
        // Another purpose on the same key ring cannot read it, and a mangled row reads as nothing, not as an exception.
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => keys.CreateProtector("SimBrokerAccount.Secrets.v1").Unprotect(stored));
        Assert.Null(vault.Unprotect(stored[..^4] + "AAAA"));
    }

    [Fact]
    public void An_account_id_is_shown_by_its_last_four_characters_only()
    {
        Assert.Equal("••••0042", PortfolioText.MaskAccount("1100000042"));
        Assert.Equal("••••", PortfolioText.MaskAccount("42"));
    }

    private static FyersPortfolioClient Fyers(FakeBroker broker, PortfolioSettings? settings = null, IBrokerCredentialsProvider? credentials = null) =>
        new(new Factory(broker), credentials ?? new Credentials(), new Monitor<PortfolioSettings>(settings ?? new PortfolioSettings()));

    private static AngelPortfolioClient Angel(FakeBroker broker) =>
        new(new Factory(broker), new Monitor<PortfolioSettings>(new PortfolioSettings { Angel = { ApiKey = "angel-key" } }),
            Options.Create(new AngelSettings()), AngelPortfolioPacer.None);
}
