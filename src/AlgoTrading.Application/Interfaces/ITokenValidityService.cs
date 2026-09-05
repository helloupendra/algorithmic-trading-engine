namespace AlgoTrading.Application.Interfaces;

/// <summary>
/// Decides whether an access token that is still within its lifetime should
/// nonetheless be refused.
/// </summary>
/// <remarks>
/// Revoking refresh tokens stops an account renewing, but the access token
/// already issued keeps working until it expires. For a disabled account that is
/// up to an hour of access after the decision to remove it. This service is what
/// makes "signed out everywhere" mean now.
/// </remarks>
public interface ITokenValidityService
{
    /// <summary>
    /// False when the account is gone or disabled, or when the token was issued
    /// before the account's cutoff.
    /// </summary>
    Task<bool> IsTokenAcceptableAsync(
        long userId,
        DateTime issuedAtUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invalidates every token this account already holds, from this moment.
    /// </summary>
    Task InvalidateExistingTokensAsync(long userId, CancellationToken cancellationToken = default);
}
