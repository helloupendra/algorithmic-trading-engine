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
/// trader passes only with a grant; a disabled account never passes. The rule
/// itself is <see cref="ModuleAccess"/>, shared with the live feed hub.
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
        var users = context.HttpContext.RequestServices.GetRequiredService<IUserAdminService>();

        switch (await ModuleAccess.CheckAsync(context.HttpContext.User, users, _moduleKey, context.HttpContext.RequestAborted))
        {
            case ModuleAccessDecision.Allowed:
                return;

            case ModuleAccessDecision.NotSignedIn:
                // Authentication itself is someone else's job; say so plainly rather
                // than reporting a missing grant for an anonymous caller.
                context.Result = new UnauthorizedResult();
                return;

            default:
                context.Result = new ObjectResult(new
                {
                    message = $"Your account does not have access to the {_moduleKey} module. Ask an admin to grant it.",
                })
                {
                    StatusCode = StatusCodes.Status403Forbidden,
                };
                return;
        }
    }
}
