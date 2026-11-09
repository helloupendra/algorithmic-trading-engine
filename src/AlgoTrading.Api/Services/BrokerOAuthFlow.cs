using System.Security.Claims;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Providers;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services;

/// <summary>Applies the same state and browser binding to every shared broker login entry point.</summary>
public sealed class BrokerOAuthFlow(BrokerOAuthStates states, IProviderRegistry registry, TradingDbContext db, TimeProvider? time = null)
{
    public async Task<string> BeginAsync(HttpContext context, IBrokerProvider broker, CancellationToken cancellationToken)
    {
        if (!context.User.IsInRole(UserRoles.Admin) || context.User.GetUserId() is not long userId ||
            context.User.GetSessionId() is not Guid sessionId ||
            !long.TryParse(context.User.FindFirstValue("iat"), out long issuedAt) ||
            !long.TryParse(context.User.FindFirstValue("exp"), out long expires))
            throw new InvalidOperationException("Sign in as an administrator before connecting the broker.");

        var issued = states.Issue(userId, sessionId, broker.Descriptor.Key, issuedAt, DateTimeOffset.FromUnixTimeSeconds(expires));
        string url = await broker.GetAuthUrlAsync(issued.State, cancellationToken: cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Cookies.Append(BrokerOAuthStates.CookieName, issued.BrowserSecret, Cookie(context.Request, BrokerOAuthStates.Lifetime));
        return url;
    }

    public async Task<IBrokerProvider> CompleteAsync(HttpContext context, string? state, CancellationToken cancellationToken)
    {
        var pending = states.Consume(state, context.Request.Cookies[BrokerOAuthStates.CookieName]);
        if (pending is null) throw new InvalidOperationException("This broker sign-in is missing, expired, already used, or belongs to another browser. Press Connect again.");
        context.Response.Cookies.Delete(BrokerOAuthStates.CookieName, Cookie(context.Request, null));
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        var session = await db.UserSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == pending.SessionId && s.UserId == pending.UserId, cancellationToken);
        var user = await db.AppUsers.AsNoTracking().SingleOrDefaultAsync(u => u.Id == pending.UserId, cancellationToken);
        if (session is null || session.RevokedUtc != null || session.ExpiresUtc <= (time ?? TimeProvider.System).GetUtcNow().UtcDateTime ||
            user is null || !user.IsActive || user.Role != UserRoles.Admin ||
            (user.TokensValidFromUtc is { } cutoff && new DateTimeOffset(DateTime.SpecifyKind(cutoff, DateTimeKind.Utc)).ToUnixTimeSeconds() >= pending.IssuedAt))
            throw new InvalidOperationException("The administrator session that started this sign-in is no longer valid. Sign in and press Connect again.");

        // Use the initiating provider, even if the default routing changed during the login.
        return registry.GetBrokerProvider(pending.ProviderKey);
    }

    private static CookieOptions Cookie(HttpRequest request, TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = maxAge,
        IsEssential = true,
    };
}
