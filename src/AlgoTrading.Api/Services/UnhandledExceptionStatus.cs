using AlgoTrading.Application.Exceptions;

namespace AlgoTrading.Api.Services;

/// <summary>
/// The status the API answers an exception that escaped an endpoint with.
/// </summary>
/// <remarks>
/// One place for it: the exception handler in Program.cs sets it on the
/// response, and <see cref="ActivityLogMiddleware"/>, which records the request
/// before that handler has run, writes down the same number the caller got.
/// </remarks>
public static class UnhandledExceptionStatus
{
    public static int For(Exception error) => error is RiskViolationException
        ? StatusCodes.Status409Conflict
        : StatusCodes.Status500InternalServerError;
}
