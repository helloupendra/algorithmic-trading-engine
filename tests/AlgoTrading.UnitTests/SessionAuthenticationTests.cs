using System.Security.Claims;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The session check every bearer token goes through. A caller that hangs up while
/// its session is being looked up must not surface as an error: JwtBearerHandler
/// logs any exception from this event as a failure, and Sentinel's log watcher
/// turned one abandoned request into an open "API error: PostgresException 57014"
/// incident (28 Nov 2026).
/// </summary>
public class SessionAuthenticationTests
{
    private sealed class Validity(Func<CancellationToken, Task<bool>> session) : ITokenValidityService
    {
        public Task<bool> IsTokenAcceptableAsync(long userId, DateTime issuedAtUtc, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> IsSessionAcceptableAsync(long userId, Guid sessionId, DateTime issuedAtUtc, CancellationToken cancellationToken = default)
            => session(cancellationToken);

        public Task InvalidateExistingTokensAsync(long userId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static TokenValidatedContext Context(ITokenValidityService validity, CancellationToken aborted)
    {
        var services = new ServiceCollection().AddSingleton(validity).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services, RequestAborted = aborted };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "7"),
            new Claim("sid", Guid.NewGuid().ToString()),
            new Claim("iat", "1790000000"),
        ], "test"));
        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        return new TokenValidatedContext(http, scheme, new JwtBearerOptions()) { Principal = principal };
    }

    [Fact]
    public async Task A_caller_that_hung_up_mid_check_is_turned_away_without_an_exception()
    {
        using var cts = new CancellationTokenSource();
        var context = Context(new Validity(token =>
        {
            cts.Cancel();   // the browser goes away while the query runs
            token.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }), cts.Token);

        await SessionAuthentication.ValidateAsync(context);

        Assert.NotNull(context.Result?.Failure);
    }

    [Fact]
    public async Task A_cancellation_the_caller_did_not_ask_for_still_surfaces()
    {
        var context = Context(new Validity(_ => throw new OperationCanceledException()), CancellationToken.None);

        await Assert.ThrowsAsync<OperationCanceledException>(() => SessionAuthentication.ValidateAsync(context));
    }

    [Fact]
    public async Task An_ended_session_is_refused()
    {
        var context = Context(new Validity(_ => Task.FromResult(false)), CancellationToken.None);

        await SessionAuthentication.ValidateAsync(context);

        Assert.Equal("This session has been ended. Sign in again.", context.Result?.Failure?.Message);
    }

    [Fact]
    public async Task A_live_session_passes()
    {
        var context = Context(new Validity(_ => Task.FromResult(true)), CancellationToken.None);

        await SessionAuthentication.ValidateAsync(context);

        Assert.Null(context.Result);
    }
}
