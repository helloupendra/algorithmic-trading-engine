using System.Security.Claims;
using AlgoTrading.Domain.Constants;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AlgoTrading.Api.Security;

/// <summary>
/// Reads the caller's identity off the validated access token.
///
/// Controllers must derive ownership from these values rather than trusting a
/// user id supplied in the request body or query string — otherwise any trader
/// could read another trader's runs simply by changing a parameter.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The authenticated user's id, or null when the token carries no usable subject.
    /// </summary>
    public static long? GetUserId(this ClaimsPrincipal principal)
    {
        // The JWT "sub" claim is remapped to NameIdentifier by the default inbound
        // claim mapping, so accept either spelling.
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? principal.FindFirstValue("sub");

        return long.TryParse(raw, out var id) ? id : null;
    }

    /// <summary>
    /// The authenticated user's id, or throws when absent. Use inside endpoints that
    /// are already <c>[Authorize]</c>d, where a missing subject means a malformed token.
    /// </summary>
    public static long GetRequiredUserId(this ClaimsPrincipal principal)
        => principal.GetUserId()
           ?? throw new UnauthorizedAccessException("Access token does not contain a user id.");

    /// <summary>
    /// The authenticated user's username, for audit fields.
    /// </summary>
    public static string? GetUserName(this ClaimsPrincipal principal)
        => principal.FindFirstValue(ClaimTypes.Name)
           ?? principal.FindFirstValue(JwtRegisteredClaimNames.UniqueName)
           ?? principal.FindFirstValue("unique_name");

    /// <summary>
    /// True when the caller holds the Admin role.
    /// </summary>
    public static bool IsAdmin(this ClaimsPrincipal principal)
        => principal.IsInRole(UserRoles.Admin);

    /// <summary>
    /// The account a read across runs covers: for an admin, the one asked for
    /// (<paramref name="requested"/>; null is every account); for anyone else,
    /// their own, whatever they asked for.
    /// </summary>
    /// <remarks>
    /// One rule for every such read (the run list, the day's P&amp;L series, the
    /// open positions, the orders), so a new one cannot scope differently by
    /// accident: a trader who passes another account's id gets their own rows,
    /// not a 403 that would confirm the account exists.
    /// </remarks>
    public static long? ScopeUserId(this ClaimsPrincipal principal, long? requested)
        => principal.IsAdmin() ? requested : principal.GetRequiredUserId();
}
