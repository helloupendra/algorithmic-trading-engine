using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Config;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Services;

/// <summary>
/// Broker credentials with a two-level resolution: the row the admin saved
/// from the console (secret encrypted with ASP.NET Data Protection) wins;
/// appsettings/.env is the fallback so existing installs work unchanged.
/// </summary>
public class DatabaseBrokerCredentialsProvider : IBrokerCredentialsProvider
{
    private const string Fyers = "FYERS";

    private readonly TradingDbContext _dbContext;
    private readonly IDataProtector _protector;
    private readonly FyersSettings _fallback;

    public DatabaseBrokerCredentialsProvider(
        TradingDbContext dbContext,
        IDataProtectionProvider dataProtectionProvider,
        IOptions<FyersSettings> fallback)
    {
        _dbContext = dbContext;
        _protector = dataProtectionProvider.CreateProtector("BrokerConfig.SecretKey.v1");
        _fallback = fallback.Value;
    }

    public async Task<BrokerCredentials> GetFyersAsync(CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.BrokerConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.BrokerName == Fyers, cancellationToken);

        if (row is not null)
        {
            return new BrokerCredentials(
                row.ClientId,
                _protector.Unprotect(row.SecretKeyEncrypted),
                row.RedirectUri,
                "database",
                row.UpdatedBy,
                row.UpdatedUtc);
        }

        bool hasFallback =
            !string.IsNullOrWhiteSpace(_fallback.ClientId) &&
            !string.IsNullOrWhiteSpace(_fallback.SecretKey);

        return new BrokerCredentials(
            _fallback.ClientId,
            _fallback.SecretKey,
            _fallback.RedirectUri,
            hasFallback ? "config" : "none",
            null,
            null);
    }

    public async Task SaveFyersAsync(
        string clientId,
        string secretKey,
        string redirectUri,
        string updatedBy,
        CancellationToken cancellationToken = default)
    {
        var row = await _dbContext.BrokerConfigs
            .FirstOrDefaultAsync(x => x.BrokerName == Fyers, cancellationToken);

        var now = DateTime.UtcNow;

        if (row is null)
        {
            row = new BrokerConfig { BrokerName = Fyers, CreatedUtc = now };
            _dbContext.BrokerConfigs.Add(row);
        }

        row.ClientId = clientId.Trim();
        row.SecretKeyEncrypted = _protector.Protect(secretKey.Trim());
        row.RedirectUri = redirectUri.Trim();
        row.UpdatedBy = updatedBy;
        row.UpdatedUtc = now;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
