using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTrading.Infrastructure.Providers.SimBroker;

/// <summary>An account as the broker's back office reports it, with the apps issued for it.</summary>
public sealed record SimBrokerAccountView(string ClientId, string Name, DateTimeOffset OpenedAt, IReadOnlyList<string> AppIds);

/// <summary>
/// Returned once, when an account is opened. The broker never shows the TOTP
/// secret again, which is why the platform stores it.
/// </summary>
public sealed record SimBrokerOpenedAccount(string ClientId, string TotpSecret, string TotpUri);

/// <summary>Returned once, when an app is issued. The secret is never shown again either.</summary>
public sealed record SimBrokerRegisteredApp(string ClientId, string AppId, string AppSecret, IReadOnlyList<string> StaticIps);

public sealed record SimBrokerKillSwitch(bool Active, DateTimeOffset? Since, DateTimeOffset? Until, string? SetBy);

/// <summary>
/// The broker's back office, as this platform uses it: open a trader's account,
/// pay money in, issue the app they trade with, and read what that account did.
/// </summary>
/// <remarks>
/// <para>
/// Authenticated with the broker's admin key, which is the whole broker rather
/// than one account. It is therefore held only on the server, never returned by
/// an endpoint, and every call made with it is made on behalf of an admin who
/// asked for it.
/// </para>
/// <para>
/// Reads go through this rather than through each trader's own credentials on
/// purpose. A trader's login costs a TOTP code, the broker refuses a code it has
/// already seen, and the codes change every 30 seconds — so a page that showed
/// twenty traders' positions would spend its life failing to sign in. The back
/// office has no such ritual, and the numbers are the same numbers.
/// </para>
/// </remarks>
public sealed class SimBrokerAdminClient
{
    private readonly IHttpClientFactory _factory;
    private readonly IOptionsMonitor<SimBrokerSettings> _settings;
    private readonly ILogger<SimBrokerAdminClient> _logger;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public SimBrokerAdminClient(
        IHttpClientFactory factory,
        IOptionsMonitor<SimBrokerSettings> settings,
        ILogger<SimBrokerAdminClient> logger)
    {
        _factory = factory;
        _settings = settings;
        _logger = logger;
    }

    public SimBrokerSettings Settings => _settings.CurrentValue;

    /// <summary>Whether the back-office key is configured at all.</summary>
    public bool IsConfigured => Settings.CanAdminister;

    /// <summary>Opens an account. The TOTP secret comes back once and only once.</summary>
    public Task<SimBrokerResult<SimBrokerOpenedAccount>> OpenAccountAsync(
        string name, string? profile = null, CancellationToken cancellationToken = default)
        => SendAsync<SimBrokerOpenedAccount>(
            HttpMethod.Post,
            "admin/accounts",
            new { name, profile },
            cancellationToken);

    /// <summary>Issues an API app for an account, whitelisted to the addresses given.</summary>
    public Task<SimBrokerResult<SimBrokerRegisteredApp>> RegisterAppAsync(
        string clientId, IReadOnlyList<string> staticIps, CancellationToken cancellationToken = default)
        => SendAsync<SimBrokerRegisteredApp>(
            HttpMethod.Post,
            $"admin/accounts/{Escape(clientId)}/apps",
            new { staticIps },
            cancellationToken);

    /// <summary>Pays money in. The broker's ledger, not the platform's capital field.</summary>
    public Task<SimBrokerResult<SimBrokerFunds>> AddFundsAsync(
        string clientId, decimal amount, string? reference, CancellationToken cancellationToken = default)
        => SendAsync<SimBrokerFunds>(
            HttpMethod.Post,
            $"admin/accounts/{Escape(clientId)}/funds",
            new { amount, reference },
            cancellationToken);

    public Task<SimBrokerResult<SimBrokerFunds>> WithdrawFundsAsync(
        string clientId, decimal amount, string? reference, CancellationToken cancellationToken = default)
        => SendAsync<SimBrokerFunds>(
            HttpMethod.Post,
            $"admin/accounts/{Escape(clientId)}/withdrawals",
            new { amount, reference },
            cancellationToken);

    public async Task<SimBrokerResult<SimBrokerAccountView>> GetAccountAsync(
        string clientId, CancellationToken cancellationToken = default)
    {
        var account = await SendAsync<AccountBody>(HttpMethod.Get, $"admin/accounts/{Escape(clientId)}", null, cancellationToken);
        return account.Succeeded && account.Value is not null
            ? SimBrokerResult<SimBrokerAccountView>.Ok(new SimBrokerAccountView(
                account.Value.ClientId,
                account.Value.Name,
                account.Value.OpenedAt,
                account.Value.Apps?.Select(a => a.AppId).ToList() ?? new List<string>()))
            : account.As<SimBrokerAccountView>();
    }

    public Task<SimBrokerResult<SimBrokerFunds>> GetFundsAsync(string clientId, CancellationToken cancellationToken = default)
        => SendAsync<SimBrokerFunds>(HttpMethod.Get, $"admin/accounts/{Escape(clientId)}/funds", null, cancellationToken);

    public Task<SimBrokerResult<IReadOnlyList<SimBrokerPosition>>> GetPositionsAsync(
        string clientId, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<SimBrokerPosition>>(
            HttpMethod.Get, $"admin/accounts/{Escape(clientId)}/positions", null, cancellationToken);

    /// <summary>The order book for one IST trading day, or today when no date is given.</summary>
    public Task<SimBrokerResult<IReadOnlyList<SimBrokerOrder>>> GetOrdersAsync(
        string clientId, DateOnly? tradingDate = null, CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<SimBrokerOrder>>(
            HttpMethod.Get,
            $"admin/accounts/{Escape(clientId)}/orders" + DateQuery(tradingDate),
            null,
            cancellationToken);

    public Task<SimBrokerResult<SimBrokerKillSwitch>> GetKillSwitchAsync(
        string clientId, CancellationToken cancellationToken = default)
        => SendAsync<SimBrokerKillSwitch>(
            HttpMethod.Get, $"admin/accounts/{Escape(clientId)}/kill-switch", null, cancellationToken);

    /// <summary>
    /// Stops an account: every working order is cancelled and, with
    /// <paramref name="squareOff"/>, every position is closed. A client cannot
    /// lift it again before 06:00 IST the next day; the back office can.
    /// </summary>
    public async Task<SimBrokerResult<SimBrokerKillSwitch>> SetKillSwitchAsync(
        string clientId, bool active, bool squareOff, CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<SimBrokerKillSwitch>(
            HttpMethod.Post,
            $"admin/accounts/{Escape(clientId)}/kill-switch",
            new { active, squareOff },
            cancellationToken);

        if (result.Succeeded)
            _logger.LogWarning("The simulated broker's kill switch for {ClientId} was set to {Active} (square off: {SquareOff}).",
                clientId, active, squareOff);

        return result;
    }

    private static string DateQuery(DateOnly? tradingDate)
        => tradingDate is null ? string.Empty : "?date=" + tradingDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Escape(string clientId) => Uri.EscapeDataString(clientId);

    private async Task<SimBrokerResult<T>> SendAsync<T>(
        HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var settings = Settings;
        if (!settings.CanAdminister)
            return SimBrokerResult<T>.Failed(
                "NOT_CONFIGURED",
                "The simulated broker's back-office key is not set: set SIMBROKER_ADMIN_KEY.");

        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Admin-Key", settings.AdminKey);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);

        HttpResponseMessage response;
        try
        {
            response = await Client().SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return SimBrokerResult<T>.Failed("UNREACHABLE", $"{settings.BaseUrl} could not be reached: {ex.Message}");
        }

        using (response)
        {
            return await SimBrokerJson.ReadAsync<T>(response, cancellationToken);
        }
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient(SimBrokerSettings.HttpClientName);
        client.BaseAddress ??= new Uri(Settings.BaseUrl.TrimEnd('/') + "/");
        return client;
    }

    private sealed record AccountBody(string ClientId, string Name, DateTimeOffset OpenedAt, List<AppBody>? Apps);

    private sealed record AppBody(string AppId, List<string>? StaticIps);
}
