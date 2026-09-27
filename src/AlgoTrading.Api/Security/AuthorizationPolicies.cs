using AlgoTrading.Domain.Constants;
using Microsoft.AspNetCore.Authorization;

namespace AlgoTrading.Api.Security;

/// <summary>
/// Named authorization policies. Referencing these constants instead of literal
/// strings means a typo is a compile error rather than a silently open endpoint.
/// </summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Restricted to the Admin role: user management, instrument import, historical
    /// backfill, strategy process control and the global kill switch.
    /// </summary>
    public const string AdminOnly = "AdminOnly";

    /// <summary>
    /// The platform's rules: every endpoint needs a signed-in caller unless it
    /// opts out with [AllowAnonymous], and <see cref="AdminOnly"/> needs the
    /// Admin role. Defined once, so a test host that drives the real
    /// attributes through the real pipeline applies the API's own rules.
    /// </summary>
    public static AuthorizationBuilder AddPlatformPolicies(this AuthorizationBuilder builder)
        => builder
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build())
            .AddPolicy(AdminOnly, policy =>
                policy.RequireRole(UserRoles.Admin));
}

/// <summary>
/// Named rate-limit policies.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Sign-in and token refresh: a sliding window per client address.</summary>
    public const string SignIn = "SignIn";
}

/// <summary>
/// Named CORS policies.
/// </summary>
public static class CorsPolicies
{
    /// <summary>
    /// The React admin/trader client. Origins come from Cors:AllowedOrigins.
    /// </summary>
    public const string WebClient = "WebClient";
}
