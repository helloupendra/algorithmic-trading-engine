namespace AlgoTrading.Contracts.Users;

/// <summary>One grantable module, as the console renders its checkbox.</summary>
public class PlatformModuleResponse
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

/// <summary>An account as the admin panel shows it.</summary>
public class UserAdminResponse
{
    public long Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>"Admin", "Trader" or "Service".</summary>
    public string Role { get; set; } = string.Empty;

    public bool IsActive { get; set; }
    public decimal TotalCapital { get; set; }

    /// <summary>Null means the platform-wide limit applies.</summary>
    public int? MaxConcurrentRuns { get; set; }

    /// <summary>Module keys this account holds. Empty for an admin, who has all of them.</summary>
    public IReadOnlyList<string> ModuleGrants { get; set; } = Array.Empty<string>();

    public long? StrategyPackageId { get; set; }
    public string? StrategyPackageName { get; set; }

    /// <summary>Extra strategies granted on top of the package.</summary>
    public IReadOnlyList<string> StrategyGrants { get; set; } = Array.Empty<string>();

    /// <summary>How many refresh tokens are live — roughly, how many devices are signed in.</summary>
    public int ActiveSessions { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
}

/// <summary>
/// A partial update: only the fields that are present are changed, so the console
/// can send one toggle without having to resend the whole account.
/// </summary>
public class UpdateUserRequest
{
    public string? Role { get; set; }
    public bool? IsActive { get; set; }
    public decimal? TotalCapital { get; set; }

    /// <summary>Send -1 to clear the override and fall back to the platform limit.</summary>
    public int? MaxConcurrentRuns { get; set; }

    /// <summary>The strategy package to put this trader on. Send -1 to remove it.</summary>
    public long? StrategyPackageId { get; set; }
}

public class SetGrantsRequest
{
    public IReadOnlyList<string> ModuleKeys { get; set; } = Array.Empty<string>();
}

public class ResetPasswordRequest
{
    public string NewPassword { get; set; } = string.Empty;
}

public class ChangeOwnPasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}
