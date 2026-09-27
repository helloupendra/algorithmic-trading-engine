using System.Security.Claims;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Domain.Constants;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// GET /api/Auth/session hands the broker's tokens to the engine only: they
/// are the keys to the owner's real brokerage account.
/// </summary>
public class BrokerTokenAccessTests
{
    private static ClaimsPrincipal As(string role) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Role, role) }, "test"));

    [Fact]
    public void Only_the_engines_service_account_reads_the_broker_tokens()
    {
        Assert.True(AuthController.MayReadBrokerTokens(As(UserRoles.Service)));
        Assert.False(AuthController.MayReadBrokerTokens(As(UserRoles.Admin)));
        Assert.False(AuthController.MayReadBrokerTokens(As(UserRoles.Trader)));
        Assert.False(AuthController.MayReadBrokerTokens(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
