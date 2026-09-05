namespace AlgoTrading.Domain.Entities;

/// <summary>
/// One module a trader is allowed to use. No row means no access.
/// </summary>
/// <remarks>
/// Deny by default: a brand-new account holds no grants, so it can sign in and
/// see nothing until an admin decides otherwise. That is what makes any signup
/// flow safe — the gate is here, not at registration.
/// </remarks>
public class UserModuleGrant
{
    public long Id { get; set; }

    public long UserId { get; set; }

    /// <summary>A key from <see cref="Constants.PlatformModules"/>.</summary>
    public string ModuleKey { get; set; } = string.Empty;

    public string GrantedBy { get; set; } = string.Empty;

    public DateTime GrantedUtc { get; set; } = DateTime.UtcNow;

    public AppUser? User { get; set; }
}
