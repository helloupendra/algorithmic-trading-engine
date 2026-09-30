using AlgoTrading.Application.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AlgoTrading.Api.Services;

/// <summary>
/// Answers an order the risk guard refused (<see cref="RiskViolationException"/>)
/// with 409 and its reason, and logs it as the refusal it is, at Warning.
/// </summary>
/// <remarks>
/// The refusal used to leave the action as an unhandled exception. The
/// caller got the same 409 from the exception handler in Program.cs, but
/// ASP.NET's ExceptionHandlerMiddleware had already logged it as
/// <c>fail: … An unhandled exception has occurred</c>, and on 30 Sep the
/// Sentinel opened a medium incident "API error: RiskViolationException: MAX
/// DAILY LOSS EXCEEDED …". A signal refused at the daily loss limit is that
/// limit working. Handled here, inside MVC, it never reaches that middleware.
/// <para>
/// The line keeps the guard's own reason, so the Sentinel's order-rate-limit
/// signature ("RATE LIMIT EXCEEDED: More than N orders …") still matches it.
/// It avoids the words the Sentinel reads as an error line.
/// </para>
/// </remarks>
public sealed class RiskRefusalFilter : IExceptionFilter
{
    private readonly ILogger<RiskRefusalFilter> _logger;

    public RiskRefusalFilter(ILogger<RiskRefusalFilter> logger)
    {
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        if (context.ExceptionHandled || context.Exception is not RiskViolationException refusal) return;

        _logger.LogWarning("Signal refused by the risk guard (409) on {Method} {Path}: {Reason}",
            context.HttpContext.Request.Method, context.HttpContext.Request.Path.Value, refusal.Message);

        context.Result = new ObjectResult(new { error = refusal.Message })
        {
            StatusCode = UnhandledExceptionStatus.For(refusal),
        };
        context.ExceptionHandled = true;
    }
}
