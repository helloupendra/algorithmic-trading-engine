using AlgoTrading.Domain.Entities;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// A FYERS token dies at 06:00 IST whatever time it was issued. The platform
/// must say so itself instead of handing the dead token to the ingestor.
/// </summary>
public class BrokerSessionExpiryTests
{
    private static BrokerSession Fyers(DateTime issuedUtc) => new()
    {
        BrokerName = "FYERS",
        ProviderKey = "fyers",
        AccessToken = "token",
        RefreshToken = "refresh",
        CreatedUtc = issuedUtc,
        UpdatedUtc = issuedUtc,
        IsActive = true,
    };

    [Fact]
    public void Token_issued_in_the_afternoon_expires_at_six_next_morning()
    {
        // Issued 2026-09-09 17:27 IST (11:57 UTC): the real cut-over day.
        var session = Fyers(new DateTime(2026, 9, 9, 11, 57, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 9, 10, 0, 30, 0, DateTimeKind.Utc), session.ExpiresAtUtc);
        // 08:45 IST next day — when market-open asked and was told "valid".
        Assert.False(session.IsAuthenticatedAt(new DateTime(2026, 9, 10, 3, 15, 0, DateTimeKind.Utc)));
        // 22:00 IST the same evening — still fine.
        Assert.True(session.IsAuthenticatedAt(new DateTime(2026, 9, 9, 16, 30, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void Token_issued_after_the_morning_sign_in_lasts_the_whole_day()
    {
        // Issued 09:27 IST (03:57 UTC): valid through the close and MCX evening, dead at 06:00 next day.
        var session = Fyers(new DateTime(2026, 9, 10, 3, 57, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 9, 11, 0, 30, 0, DateTimeKind.Utc), session.ExpiresAtUtc);
        Assert.True(session.IsAuthenticatedAt(new DateTime(2026, 9, 10, 18, 0, 0, DateTimeKind.Utc)));   // 23:30 IST
        Assert.False(session.IsAuthenticatedAt(new DateTime(2026, 9, 11, 0, 30, 0, DateTimeKind.Utc)));  // 06:00 IST sharp
    }

    [Fact]
    public void Token_issued_before_six_expires_at_six_the_same_morning()
    {
        // 05:30 IST sign-in: conservative — treated as dead at 06:00 the same day.
        var session = Fyers(new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 9, 10, 0, 30, 0, DateTimeKind.Utc), session.ExpiresAtUtc);
    }

    [Fact]
    public void Inactive_or_empty_sessions_are_never_authenticated()
    {
        var now = new DateTime(2026, 9, 10, 5, 0, 0, DateTimeKind.Utc);
        var loggedOut = Fyers(now.AddHours(-1)); loggedOut.IsActive = false;
        var empty = Fyers(now.AddHours(-1)); empty.AccessToken = "";
        Assert.False(loggedOut.IsAuthenticatedAt(now));
        Assert.False(empty.IsAuthenticatedAt(now));
    }

    [Fact]
    public void Unknown_broker_has_no_expiry_rule_and_is_trusted()
    {
        var session = Fyers(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
        session.ProviderKey = "someotherbroker";
        session.BrokerName = "Other";
        Assert.Null(session.ExpiresAtUtc);
        Assert.True(session.IsAuthenticatedAt(new DateTime(2026, 9, 10, 5, 0, 0, DateTimeKind.Utc)));
    }
}
