using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Providers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Broker credentials with a two-level resolution: the row the admin saved
/// from the console (secret encrypted with ASP.NET Data Protection) wins;
/// appsettings/.env is the fallback so existing installs work unchanged.
/// </summary>
public class DatabaseBrokerCredentialsProvider : IBrokerCredentialsProvider
{
    private readonly TradingDbContext _dbContext;
    private readonly IDataProtector _protector;
    private readonly ProviderCredentialFallbacks _fallbacks;

    public DatabaseBrokerCredentialsProvider(
        TradingDbContext dbContext,
        IDataProtectionProvider dataProtectionProvider,
        ProviderCredentialFallbacks fallbacks)
    {
        _dbContext = dbContext;
        _protector = dataProtectionProvider.CreateProtector("BrokerConfig.SecretKey.v1");
        _fallbacks = fallbacks;
    }

    /// <summary>
    /// The broker_configs rows predate provider keys and hold the broker name in
    /// upper case ("FYERS"), so that is the stored form of a provider key.
    /// </summary>
    private static string ToBrokerName(string providerKey) => providerKey.Trim().ToUpperInvariant();

    public async Task<BrokerCredentials> GetAsync(
        string providerKey,
        long? brokerAccountId = null,
        CancellationToken cancellationToken = default)
    {
        string brokerName = ToBrokerName(providerKey);

        var row = await _dbContext.BrokerConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.BrokerName == brokerName && x.BrokerAccountId == brokerAccountId,
                cancellationToken);

        if (row is not null)
        {
            return new BrokerCredentials(
                row.ClientId,
                _protector.Unprotect(row.SecretKeyEncrypted),
                row.RedirectUri,
                // Empty means "never saved"; unprotecting an empty string would throw.
                string.IsNullOrEmpty(row.TradingPinEncrypted) ? null : _protector.Unprotect(row.TradingPinEncrypted),
                "database",
                row.UpdatedBy,
                row.UpdatedUtc);
        }

        return _fallbacks.Find(providerKey);
    }

    public async Task SaveAsync(
        string providerKey,
        string clientId,
        string secretKey,
        string redirectUri,
        string updatedBy,
        string? tradingPin = null,
        long? brokerAccountId = null,
        CancellationToken cancellationToken = default)
    {
        string brokerName = ToBrokerName(providerKey);

        var row = await _dbContext.BrokerConfigs
            .FirstOrDefaultAsync(
                x => x.BrokerName == brokerName && x.BrokerAccountId == brokerAccountId,
                cancellationToken);

        var now = DateTime.UtcNow;

        if (row is null)
        {
            row = new BrokerConfig
            {
                BrokerName = brokerName,
                BrokerAccountId = brokerAccountId,
                CreatedUtc = now,
            };
            _dbContext.BrokerConfigs.Add(row);
        }

        row.ClientId = clientId.Trim();
        row.SecretKeyEncrypted = _protector.Protect(secretKey.Trim());
        row.RedirectUri = redirectUri.Trim();

        // Null means "leave it as it was": re-saving the app credentials from
        // the console must not silently wipe the PIN and break the next
        // unattended morning.
        if (tradingPin is not null)
        {
            row.TradingPinEncrypted = string.IsNullOrWhiteSpace(tradingPin)
                ? string.Empty
                : _protector.Protect(tradingPin.Trim());
        }

        row.UpdatedBy = updatedBy;
        row.UpdatedUtc = now;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
