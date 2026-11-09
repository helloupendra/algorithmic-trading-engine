using System.Security.Claims;
using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace AlgoTrading.Api.Security;

public static class SessionAuthentication
{
    public static async Task ValidateAsync(TokenValidatedContext context)
    {
        var principal = context.Principal;
        if (principal?.GetUserId() is not long userId || principal.GetSessionId() is not Guid sessionId ||
            !long.TryParse(principal.FindFirstValue("iat"), out var seconds) || seconds < 0 || seconds > 253402300799)
        {
            context.Fail("This token has no valid login session. Sign in again.");
            return;
        }
        var validity = context.HttpContext.RequestServices.GetRequiredService<ITokenValidityService>();
        if (!await validity.IsSessionAcceptableAsync(userId, sessionId, DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime, context.HttpContext.RequestAborted))
            context.Fail("This session has been ended. Sign in again.");
    }
}
