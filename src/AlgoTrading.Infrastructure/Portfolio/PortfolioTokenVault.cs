using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace AlgoTrading.Infrastructure.Portfolio;

/// <summary>
/// Encrypts a user's broker token for <c>portfolio_connections</c> with ASP.NET Data Protection, under a purpose of its
/// own, so a token can only be read back by this code and not by any other protector on the key ring.
/// </summary>
public sealed class PortfolioTokenVault
{
    /// <summary>The Data Protection purpose. A new version would get a new string, and old rows would read as unreadable.</summary>
    public const string Purpose = "PortfolioConnection.Token.v1";

    private readonly IDataProtector _protector;

    public PortfolioTokenVault(IDataProtectionProvider dataProtection)
    {
        _protector = dataProtection.CreateProtector(Purpose);
    }

    public string Protect(PortfolioToken token) => _protector.Protect(JsonSerializer.Serialize(token));

    /// <summary>
    /// The token, or null when the stored text cannot be read (the key ring was replaced, the row was edited): the
    /// user connects again rather than the read failing with a cryptography error.
    /// </summary>
    public PortfolioToken? Unprotect(string? protectedToken)
    {
        if (string.IsNullOrWhiteSpace(protectedToken)) return null;
        try
        {
            var token = JsonSerializer.Deserialize<PortfolioToken>(_protector.Unprotect(protectedToken));
            return string.IsNullOrWhiteSpace(token?.AccessToken) ? null : token;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }
}
