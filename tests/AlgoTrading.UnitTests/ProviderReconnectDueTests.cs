using AlgoTrading.Api.Controllers;
using AlgoTrading.Application.Providers;
using AlgoTrading.Domain.Entities;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// When a connector says a reconnect is due. The checkup, the desk's status strip
/// and the Connectors page all read this flag, and a person presses Sign in on it.
/// </summary>
public class ProviderReconnectDueTests
{
    private static DateTime Ist(int day, int hour, int minute) =>
        new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Utc).AddMinutes(-330);

    private static bool Due(string provider, DateTime saved, DateTime now) =>
        ProvidersController.IsReconnectDue(
            ProviderAuthKind.OAuthDaily, saved, BrokerSession.TokenExpiryUtc(provider, null, saved), now);

    [Fact]
    public void A_Dhan_token_from_yesterday_with_time_left_is_not_due()
    {
        // 4 Nov: saved 09:06 the day before, checked at 08:55, good to 09:06.
        Assert.False(Due("dhan", Ist(3, 9, 6), Ist(4, 8, 55)));
    }

    [Fact]
    public void A_Dhan_token_is_due_from_the_moment_it_ends()
    {
        Assert.False(Due("dhan", Ist(3, 9, 6), Ist(4, 9, 5)));
        Assert.True(Due("dhan", Ist(3, 9, 6), Ist(4, 9, 6)));
    }

    [Fact]
    public void A_FYERS_token_from_last_night_is_due_at_six_in_the_morning()
    {
        Assert.False(Due("fyers", Ist(3, 20, 0), Ist(4, 0, 10)));
        Assert.False(Due("fyers", Ist(3, 20, 0), Ist(4, 5, 59)));
        Assert.True(Due("fyers", Ist(3, 20, 0), Ist(4, 6, 0)));
    }

    [Fact]
    public void With_no_known_end_a_daily_token_is_due_once_the_IST_date_has_passed()
    {
        Assert.False(ProvidersController.IsReconnectDue(ProviderAuthKind.OAuthDaily, Ist(3, 23, 50), null, Ist(3, 23, 59)));
        Assert.True(ProvidersController.IsReconnectDue(ProviderAuthKind.OAuthDaily, Ist(3, 23, 50), null, Ist(4, 0, 10)));
    }

    [Fact]
    public void A_connector_without_a_daily_token_is_never_due()
    {
        Assert.False(ProvidersController.IsReconnectDue(ProviderAuthKind.ApiKey, Ist(1, 9, 0), null, Ist(4, 9, 0)));
    }
}
