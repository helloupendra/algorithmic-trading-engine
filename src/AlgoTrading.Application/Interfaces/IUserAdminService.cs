using AlgoTrading.Contracts.Users;

namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Everything an admin does to accounts, and the one question every guarded
/// endpoint asks: may this user use this module?
/// </summary>
public interface IUserAdminService
{
    /// <summary>Every account, with its grants, session count and last sign-in.</summary>
    Task<IReadOnlyList<UserAdminResponse>> ListAsync(CancellationToken cancellationToken = default);

    Task<UserAdminResponse?> GetAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes role, capital, run cap or active state. Only the fields present on
    /// the request are touched.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The change would leave the platform with no admin, or the caller is
    /// demoting or disabling themselves.
    /// </exception>
    Task<UserAdminResponse> UpdateAsync(
        long userId,
        UpdateUserRequest request,
        long actingUserId,
        string actingUserName,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces this account's grants with exactly the keys given.</summary>
    Task<UserAdminResponse> SetGrantsAsync(
        long userId,
        IReadOnlyList<string> moduleKeys,
        string actingUserName,
        CancellationToken cancellationToken = default);

    /// <summary>Sets a new password and signs the account out everywhere.</summary>
    Task ResetPasswordAsync(
        long userId,
        string newPassword,
        string actingUserName,
        CancellationToken cancellationToken = default);

    /// <summary>Changes the caller's own password after checking the current one.</summary>
    /// <exception cref="InvalidOperationException">The current password is wrong.</exception>
    Task ChangeOwnPasswordAsync(
        long userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes every refresh token for an account, ending its sessions.</summary>
    Task<int> RevokeSessionsAsync(long userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the user may use the module: admins always, everyone else only
    /// with a grant. A disabled account is never allowed.
    /// </summary>
    Task<bool> IsModuleAllowedAsync(
        long userId,
        string moduleKey,
        CancellationToken cancellationToken = default);
}
