namespace AlgoTrading.Domain.Constants;

/// <summary>
/// The authorization roles recognised across the platform.
///
/// Stored as a plain string on <see cref="Entities.AppUser.Role"/> and emitted as a
/// <c>role</c> claim on the access token, so the same literals drive both
/// <c>[Authorize(Roles = ...)]</c> on the API and route guards in the web client.
/// </summary>
public static class UserRoles
{
    /// <summary>
    /// Full control: user management, instrument import, the global kill switch,
    /// and starting or stopping any strategy for any user.
    /// </summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Standard trading account. Sees only its own simulation runs, orders,
    /// positions and P&amp;L, and may start or stop only its own strategies.
    /// </summary>
    public const string Trader = "Trader";

    /// <summary>
    /// Every role, for validation and for seeding the admin panel's role picker.
    /// </summary>
    public static readonly string[] All = { Admin, Trader };

    /// <summary>
    /// Returns the canonical casing for <paramref name="role"/>, or null when it is
    /// not a recognised role. Comparison is case-insensitive so that input from the
    /// admin panel or a seed file does not have to match exactly.
    /// </summary>
    public static string? Normalize(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            return null;
        }

        foreach (var known in All)
        {
            if (string.Equals(known, role.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="role"/> names a role the platform knows about.
    /// </summary>
    public static bool IsValid(string? role) => Normalize(role) is not null;
}
