using AlgoTrading.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AlgoTrading.Api.Security;

/// <summary>
/// Refuses the request unless the caller may use this module.
/// </summary>
/// <remarks>
/// Hiding a menu entry is not access control — a trader can type the URL — so the
/// grant is checked here, on the endpoint, every time. Admins pass by role; a
/// trader passes only with a grant; a disabled account never passes.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequireModuleAttribute : Attribute, IAsyncAuthorizationFilter
{
    private readonly string _moduleKey;

    public RequireModuleAttribute(string moduleKey)
    {
        _moduleKey = moduleKey;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;

        if (user?.Identity?.IsAuthenticated != true)
        {
            // Authentication itself is someone else's job; say so plainly rather
            // than reporting a missing grant for an anonymous caller.
            context.Result = new UnauthorizedResult();
            return;
        }

        long? userId = user.GetUserId();

        if (userId is null)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var users = context.HttpContext.RequestServices.GetRequiredService<IUserAdminService>();

        if (await users.IsModuleAllowedAsync(userId.Value, _moduleKey, context.HttpContext.RequestAborted))
        {
            return;
        }

        context.Result = new ObjectResult(new
        {
            message = $"Your account does not have access to the {_moduleKey} module. Ask an admin to grant it.",
        })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }
}
