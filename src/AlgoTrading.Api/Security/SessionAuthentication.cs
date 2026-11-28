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
        var aborted = context.HttpContext.RequestAborted;
        bool acceptable;
        try
        {
            acceptable = await validity.IsSessionAcceptableAsync(userId, sessionId, DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime, aborted);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // The caller hung up while its session was being looked up — a tab closed or a poll superseded.
            // There is no one left to authenticate and nothing went wrong. Left to escape, JwtBearerHandler logs
            // it as a failure and Sentinel's log watcher opens an incident for a database error that never was.
            context.Fail("The request was aborted while its session was being checked.");
            return;
        }
        if (!acceptable)
            context.Fail("This session has been ended. Sign in again.");
    }
}
