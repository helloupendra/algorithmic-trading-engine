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
