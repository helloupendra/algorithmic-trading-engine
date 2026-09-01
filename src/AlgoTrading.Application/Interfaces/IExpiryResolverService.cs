// src/AlgoTrading.Application/Interfaces/IExpiryResolverService.cs
using AlgoTrading.Contracts.Expiry;

namespace AlgoTrading.Application.Interfaces;

public interface IExpiryResolverService
{
    Task<ExpiryRuleResponse?> GetRuleAsync(
        string exchange,
        string underlying,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DateOnly>> GetAvailableExpiriesAsync(
        string exchange,
        string underlying,
        CancellationToken cancellationToken = default);

    Task<ResolvedExpiryResponse?> ResolvePreferredExpiryAsync(
        string exchange,
        string underlying,
        DateTime utcNow,
        CancellationToken cancellationToken = default);

    Task<ResolvedExpiryResponse?> ResolveExactExpiryAsync(
        string exchange,
        string underlying,
        string expiryType,
        DateTime utcNow,
        CancellationToken cancellationToken = default);
}