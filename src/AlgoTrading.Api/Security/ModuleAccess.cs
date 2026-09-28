using System.Security.Claims;
using AlgoTrading.Application.Interfaces;

namespace AlgoTrading.Api.Security;

/// <summary>What <see cref="ModuleAccess.CheckAsync"/> decided.</summary>
public enum ModuleAccessDecision
{
    Allowed,

    /// <summary>No signed-in caller, or a token without a usable account id.</summary>
    NotSignedIn,

    /// <summary>Signed in, but neither an admin nor holding the grant (or the account is disabled).</summary>
    NotGranted
}

/// <summary>
/// Whether a caller may use a module: the one rule behind
/// <see cref="RequireModuleAttribute"/> on the REST endpoints and behind the
/// live feed hub's <see cref="Hubs.LiveFeedHub.SubscribeAll"/>.
/// </summary>
/// <remarks>
/// One place, so a websocket can never be looser (or stricter) than the
/// endpoints serving the same data. The account row decides, not the token:
/// admins by role, everyone else by a grant, a disabled account never — and a
/// grant revoked a minute ago is revoked now, not when the token expires.
/// </remarks>
public static class ModuleAccess
{
    public static async Task<ModuleAccessDecision> CheckAsync(
        ClaimsPrincipal? user,
        IUserAdminService users,
        string moduleKey,
        CancellationToken cancellationToken)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return ModuleAccessDecision.NotSignedIn;

        if (user.GetUserId() is not { } userId)
            return ModuleAccessDecision.NotSignedIn;

        return await users.IsModuleAllowedAsync(userId, moduleKey, cancellationToken)
            ? ModuleAccessDecision.Allowed
            : ModuleAccessDecision.NotGranted;
    }
}
