using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlgoTrading.Infrastructure.Providers.SimBroker;

/// <summary>The platform's record of a trader's account, with no secret in it.</summary>
public sealed record SimBrokerAccountLink(
    long UserId,
    string ClientId,
    string AppId,
    IReadOnlyList<string> StaticIps,
    bool IsEnabled,
    string CreatedBy,
    DateTime CreatedUtc);

/// <summary>An account's credentials, handed out only when someone explicitly asks for them.</summary>
public sealed record SimBrokerCredentials(string ClientId, string AppId, string AppSecret, string TotpSecret, string TotpUri);

/// <summary>Everything a page shows about one trader's account, read from the broker each time.</summary>
public sealed record SimBrokerAccountSnapshot(
    SimBrokerAccountLink Link,
    SimBrokerFunds? Funds,
    IReadOnlyList<SimBrokerPosition> Positions,
    IReadOnlyList<SimBrokerOrder> Orders,
    SimBrokerKillSwitch? KillSwitch,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Gives each trader their own account at the simulated broker, and reads what
/// that account is doing.
/// </summary>
/// <remarks>
/// <para>
/// One trader, one account. The platform keeps only the link and the
/// credentials; the money, the orders and the positions are read from the
/// broker on every request, so the console can never show a stale balance it
/// cached itself.
/// </para>
/// <para>
/// Issuing is three calls to the broker — open the account, issue the app,
/// pay the opening money in — and they cannot be one transaction, because the
/// broker is a separate system with its own journal. If a later step fails the
/// earlier ones stand, so the account is saved as soon as there is something to
/// save and the failure is reported plainly rather than rolled back by pretending.
/// </para>
/// </remarks>
public sealed class SimBrokerAccountService
{
    private readonly TradingDbContext _db;
    private readonly SimBrokerAdminClient _admin;
    private readonly SimBrokerClient _client;
    private readonly IDataProtector _protector;
    private readonly ILogger<SimBrokerAccountService> _logger;

    public SimBrokerAccountService(
        TradingDbContext db,
        SimBrokerAdminClient admin,
        SimBrokerClient client,
        IDataProtectionProvider dataProtection,
        ILogger<SimBrokerAccountService> logger)
    {
        _db = db;
        _admin = admin;
        _client = client;
        _protector = dataProtection.CreateProtector("SimBrokerAccount.Secrets.v1");
        _logger = logger;
    }

    public bool CanAdminister => _admin.IsConfigured;

    /// <summary>Every trader's link, for the admin list. No secrets.</summary>
    public async Task<IReadOnlyList<SimBrokerAccountLink>> ListAsync(CancellationToken cancellationToken = default)
        => (await _db.SimBrokerAccounts.AsNoTracking().OrderBy(a => a.UserId).ToListAsync(cancellationToken))
            .Select(ToLink).ToList();

    public async Task<SimBrokerAccountLink?> FindAsync(long userId, CancellationToken cancellationToken = default)
    {
        var row = await _db.SimBrokerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        return row is null ? null : ToLink(row);
    }

    /// <summary>
    /// Opens an account for a trader, issues the app they sign in with, and
    /// pays the opening money in.
    /// </summary>
    /// <param name="openingFunds">Rupees to pay in, or zero for an account that starts empty.</param>
    public async Task<SimBrokerResult<SimBrokerAccountLink>> IssueAsync(
        long userId,
        string traderName,
        decimal openingFunds,
        string issuedBy,
        CancellationToken cancellationToken = default)
    {
        if (await _db.SimBrokerAccounts.AnyAsync(a => a.UserId == userId, cancellationToken))
            return SimBrokerResult<SimBrokerAccountLink>.Failed(
                "ALREADY_LINKED", "This trader already has an account at the simulated broker.");

        if (!_admin.IsConfigured)
            return SimBrokerResult<SimBrokerAccountLink>.Failed(
                "NOT_CONFIGURED", "The simulated broker's back-office key is not set: set SIMBROKER_ADMIN_KEY.");

        var opened = await _admin.OpenAccountAsync(traderName, profile: null, cancellationToken);
        if (!opened.Succeeded || opened.Value is null) return opened.As<SimBrokerAccountLink>();

        // The app may only be used from the addresses named here. The platform
        // calls the broker on the trader's behalf, so the address that matters
        // is this server's — asked of the broker itself, because what it sees
        // through the tunnel is the only answer its own check will agree with.
        var addresses = await WhitelistAsync(cancellationToken);

        var app = await _admin.RegisterAppAsync(opened.Value.ClientId, addresses, cancellationToken);
        if (!app.Succeeded || app.Value is null)
        {
            _logger.LogError(
                "Opened simulated-broker account {ClientId} for user {UserId}, but issuing its app failed: {Code} {Message}. "
                + "The account exists at the broker and has no app.",
                opened.Value.ClientId, userId, app.ErrorCode, app.ErrorMessage);
            return app.As<SimBrokerAccountLink>();
        }

        var row = new SimBrokerAccount
        {
            UserId = userId,
            ClientId = opened.Value.ClientId,
            AppId = app.Value.AppId,
            AppSecretProtected = _protector.Protect(app.Value.AppSecret),
            TotpSecretProtected = _protector.Protect(opened.Value.TotpSecret),
            StaticIps = string.Join(",", app.Value.StaticIps),
            CreatedBy = issuedBy,
        };

        // Saved before the money goes in: a funding failure must not lose the
        // only copy of secrets the broker will never show again.
        _db.SimBrokerAccounts.Add(row);
        await _db.SaveChangesAsync(cancellationToken);

        if (openingFunds > 0)
        {
            var funded = await _admin.AddFundsAsync(row.ClientId, openingFunds, $"Opening balance for {traderName}", cancellationToken);
            if (!funded.Succeeded)
                _logger.LogError("Account {ClientId} was opened for user {UserId} but the opening ₹{Amount} did not go in: {Code} {Message}.",
                    row.ClientId, userId, openingFunds, funded.ErrorCode, funded.ErrorMessage);
        }

        _logger.LogInformation("Simulated-broker account {ClientId} issued to user {UserId} by {IssuedBy}.",
            row.ClientId, userId, issuedBy);

        return SimBrokerResult<SimBrokerAccountLink>.Ok(ToLink(row));
    }

    /// <summary>Pays money in, or out when <paramref name="amount"/> is negative.</summary>
    public async Task<SimBrokerResult<SimBrokerFunds>> PayAsync(
        long userId, decimal amount, string? reference, CancellationToken cancellationToken = default)
    {
        var row = await _db.SimBrokerAccounts.FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        if (row is null) return NotLinked<SimBrokerFunds>();
        if (amount == 0) return SimBrokerResult<SimBrokerFunds>.Failed("INVALID_REQUEST", "An amount of zero moves nothing.");

        return amount > 0
            ? await _admin.AddFundsAsync(row.ClientId, amount, reference, cancellationToken)
            : await _admin.WithdrawFundsAsync(row.ClientId, -amount, reference, cancellationToken);
    }

    /// <summary>
    /// Stops or restarts a trader's account at the broker. Stopping cancels
    /// every working order and, with <paramref name="squareOff"/>, closes every
    /// position.
    /// </summary>
    public async Task<SimBrokerResult<SimBrokerKillSwitch>> SetKillSwitchAsync(
        long userId, bool active, bool squareOff, CancellationToken cancellationToken = default)
    {
        var row = await _db.SimBrokerAccounts.FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        return row is null
            ? NotLinked<SimBrokerKillSwitch>()
            : await _admin.SetKillSwitchAsync(row.ClientId, active, squareOff, cancellationToken);
    }

    /// <summary>
    /// Whether the platform offers this trader their account. It does not touch
    /// the account at the broker: money and positions are not something a
    /// checkbox here should be able to destroy.
    /// </summary>
    public async Task<bool> SetEnabledAsync(long userId, bool enabled, CancellationToken cancellationToken = default)
    {
        var row = await _db.SimBrokerAccounts.FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        if (row is null) return false;

        row.IsEnabled = enabled;
        row.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// The credentials themselves, for a trader who wants to call the broker's
    /// API from their own code. Handed out only on an explicit request, never as
    /// part of a page's own data.
    /// </summary>
    public async Task<SimBrokerCredentials?> RevealAsync(long userId, CancellationToken cancellationToken = default)
    {
        var row = await _db.SimBrokerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        if (row is null) return null;

        string totp = _protector.Unprotect(row.TotpSecretProtected);
        string issuer = Uri.EscapeDataString("OpenFNO Broker");
        string label = Uri.EscapeDataString(row.ClientId);
        return new SimBrokerCredentials(
            row.ClientId,
            row.AppId,
            _protector.Unprotect(row.AppSecretProtected),
            totp,
            $"otpauth://totp/{issuer}:{label}?secret={totp}&issuer={issuer}");
    }

    /// <summary>
    /// What the trader's account is doing right now: money, positions, today's
    /// orders and whether it is stopped.
    /// </summary>
    /// <remarks>
    /// One failed part does not fail the whole snapshot — a trader whose
    /// positions cannot be read should still see their balance, with the reason
    /// the rest is missing said out loud rather than shown as zero.
    /// </remarks>
    public async Task<SimBrokerResult<SimBrokerAccountSnapshot>> SnapshotAsync(
        long userId, DateOnly? tradingDate = null, CancellationToken cancellationToken = default)
    {
        var row = await _db.SimBrokerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken);
        if (row is null) return NotLinked<SimBrokerAccountSnapshot>();

        var warnings = new List<string>();

        var funds = await _admin.GetFundsAsync(row.ClientId, cancellationToken);
        if (!funds.Succeeded) warnings.Add($"Funds: {funds.ErrorMessage}");

        var positions = await _admin.GetPositionsAsync(row.ClientId, cancellationToken);
        if (!positions.Succeeded) warnings.Add($"Positions: {positions.ErrorMessage}");

        var orders = await _admin.GetOrdersAsync(row.ClientId, tradingDate, cancellationToken);
        if (!orders.Succeeded) warnings.Add($"Orders: {orders.ErrorMessage}");

        var kill = await _admin.GetKillSwitchAsync(row.ClientId, cancellationToken);
        if (!kill.Succeeded) warnings.Add($"Kill switch: {kill.ErrorMessage}");

        return SimBrokerResult<SimBrokerAccountSnapshot>.Ok(new SimBrokerAccountSnapshot(
            ToLink(row),
            funds.Value,
            positions.Value ?? Array.Empty<SimBrokerPosition>(),
            orders.Value ?? Array.Empty<SimBrokerOrder>(),
            kill.Value,
            warnings));
    }

    /// <summary>
    /// The addresses an issued app may call from: what the broker sees for this
    /// server, plus anything configured by hand for a second machine.
    /// </summary>
    private async Task<IReadOnlyList<string>> WhitelistAsync(CancellationToken cancellationToken)
    {
        var addresses = new List<string>();

        var seen = await _client.WhoAmIAsync(cancellationToken);
        if (seen.Succeeded && !string.IsNullOrWhiteSpace(seen.Value))
            addresses.Add(seen.Value!);
        else
            _logger.LogWarning("The simulated broker did not say which address it sees for this server: {Message}", seen.ErrorMessage);

        string configured = _admin.Settings.StaticIp;
        foreach (var extra in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!addresses.Contains(extra))
                addresses.Add(extra);

        return addresses;
    }

    private static SimBrokerResult<T> NotLinked<T>()
        => SimBrokerResult<T>.Failed("NOT_LINKED", "This trader has no account at the simulated broker yet.");

    private static SimBrokerAccountLink ToLink(SimBrokerAccount row) => new(
        row.UserId,
        row.ClientId,
        row.AppId,
        row.StaticIps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        row.IsEnabled,
        row.CreatedBy,
        row.CreatedUtc);
}
