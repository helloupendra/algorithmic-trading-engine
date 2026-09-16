using AlgoTrading.Application.Providers;
using AlgoTrading.Infrastructure.Providers.Angel;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The Angel One connector, added on 2026-09-16 while FYERS and Dhan were
/// feeding the live desk. These pin the three things that decide whether the
/// console tells the truth about it: the TOTP its login needs, what counts as
/// configured, and that the descriptor claims nothing it has not built.
/// </summary>
public class AngelConnectorTests
{
    [Theory]
    // RFC 6238's own vectors: secret "12345678901234567890" in base32.
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1234567890L, "89005924")]
    public void Totp_matches_the_rfc_vectors(long epochSeconds, string expected)
    {
        string code = AngelTotp.Generate("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ",
            DateTimeOffset.FromUnixTimeSeconds(epochSeconds), digits: 8);
        Assert.Equal(expected, code);
    }

    [Fact]
    public void Totp_accepts_the_secret_as_angel_shows_it()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(59);
        Assert.Equal(AngelTotp.Generate("GEZDGNBVGY3TQOJQ", at),
                     AngelTotp.Generate("gezd gnbv gy3t qojq", at));
        Assert.Equal(AngelTotp.Generate("GEZDGNBVGY3TQOJQ", at),
                     AngelTotp.Generate("GEZDGNBVGY3TQOJQ====", at));
    }

    [Fact]
    public void An_empty_or_invalid_secret_names_the_setting_to_fix()
    {
        var empty = Assert.Throws<ArgumentException>(() => AngelTotp.Generate(""));
        Assert.Contains("ANGEL_TOTP_SECRET", empty.Message);
        var bad = Assert.Throws<ArgumentException>(() => AngelTotp.Generate("not-base32-1"));
        Assert.Contains("ANGEL_TOTP_SECRET", bad.Message);
    }

    [Fact]
    public void Missing_credentials_are_listed_by_the_name_an_operator_sets()
    {
        var settings = new AngelSettings { ApiKey = "k" };
        Assert.False(settings.IsConfigured);
        Assert.Equal(new[] { "ANGEL_CLIENT_CODE", "ANGEL_PIN", "ANGEL_TOTP_SECRET" }, settings.Missing());

        settings.ClientCode = "A1";
        settings.Pin = "1234";
        settings.TotpSecret = "GEZDGNBVGY3TQOJQ";
        Assert.True(settings.IsConfigured);
        Assert.Empty(settings.Missing());
    }

    [Fact]
    public void The_descriptor_claims_only_what_is_built()
    {
        var d = AngelProvider.Descriptor;
        Assert.Equal("angel", d.Key);
        Assert.Equal(ProviderKind.Data, d.Kind);
        Assert.Equal(ProviderAuthKind.ApiKey, d.Auth);

        // Built: REST history, quotes, historical OI, live greeks.
        Assert.True(d.Capabilities.History);
        Assert.True(d.Capabilities.Quotes);
        Assert.True(d.Capabilities.OpenInterest);
        Assert.True(d.Capabilities.Greeks);

        // Not built: no feed adapter, no full chain, no order path.
        Assert.False(d.Capabilities.LiveTicks);
        Assert.False(d.Capabilities.Depth);
        Assert.False(d.Capabilities.OptionChain);
        Assert.False(d.Capabilities.Orders);

        // The 1-minute history window, which is what a backfill must respect.
        Assert.Equal(30, d.Capabilities.HistoryMaxDaysPerCall);
    }

    [Theory]
    [InlineData("Invalid totp", "AB1050", "clock")]
    [InlineData("Client is blocked", "AB1004", "static IP")]
    [InlineData("Invalid Token", "AB1010", "signs in again")]
    [InlineData("Something else", "", "Something else")]
    public void A_refusal_carries_the_vendors_words_and_the_cause(string message, string code, string expected)
    {
        Assert.Contains(expected, AngelApiClient.Explain(message, code));
    }
}

/// <summary>
/// Pace, not credentials: SmartAPI refuses a burst with HTTP 403 and a
/// plain-text body. The movers screen makes six calls, and on 2026-09-16 three
/// of them came back as "something that is not JSON" until this was handled.
/// </summary>
public class AngelRateLimitTests
{
    [Theory]
    [InlineData(403, "Access denied because of exceeding access rate", true)]
    [InlineData(429, "Too many requests, rate limit", true)]
    [InlineData(403, "Forbidden: bad token", false)]
    [InlineData(200, "Access denied because of exceeding access rate", false)]
    public void A_rate_refusal_is_told_apart_from_a_real_one(int status, string body, bool expected)
    {
        Assert.Equal(expected, AngelApiClient.IsRateLimit(status, body));
    }

    [Fact]
    public void Calls_are_spaced_and_retried_at_a_pace_the_vendor_accepts()
    {
        Assert.True(AngelApiClient.MinCallGap >= TimeSpan.FromSeconds(1));
        Assert.NotEmpty(AngelApiClient.RateLimitWaits);
        Assert.True(AngelApiClient.RateLimitWaits[0] >= TimeSpan.FromSeconds(1));
    }
}
