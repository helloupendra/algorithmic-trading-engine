using System.Text.RegularExpressions;
using AlgoTrading.Contracts.Portfolio;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Portfolio;
using AlgoTrading.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Services.Portfolio;

/// <summary>What the broker's redirect carried, by the names each broker uses.</summary>
/// <param name="AuthCode">FYERS's <c>auth_code</c>.</param>
/// <param name="Status">FYERS's <c>s</c>: "ok" or "error".</param>
/// <param name="AuthToken">Angel One's <c>auth_token</c>, the session's JWT.</param>
public sealed record PortfolioRedirect(string? State, string? AuthCode, string? Status, string? AuthToken);

/// <summary>How a link ended: the user it was for (when the state said), and a short message for the page.</summary>
public sealed record PortfolioLinkOutcome(bool Ok, long? UserId, string Message);

/// <summary>How a start went: what the page does next, or why it cannot.</summary>
/// <param name="State">For a redirect: the state the browser's cookie must carry back.</param>
public sealed record PortfolioStartOutcome(PortfolioStartResponse? Response, string? State, string? Error);

/// <summary>
/// A user's links to their own broker accounts: listing them, starting and finishing a sign-in, a pasted token, and
/// disconnecting. Every method takes the user id the controller read from the sign-in token, never one from a request.
/// </summary>
/// <remarks>
/// A link is saved only after one read with the new token works (the broker's profile): a token that opens nothing
/// is never stored, and the account it opened is the one shown, masked to its last four characters. The token itself
/// is stored encrypted (<see cref="PortfolioTokenVault"/>) and never returned.
/// </remarks>
public sealed partial class PortfolioLinkService(
    TradingDbContext db,
    FyersPortfolioClient fyers,
    DhanPortfolioClient dhan,
    AngelPortfolioClient angel,
    PortfolioTokenVault vault,
    PortfolioLinkStates states,
    ILogger<PortfolioLinkService> logger,
    TimeProvider? time = null)
{
    public const string ModeRedirect = "redirect";
    public const string ModeToken = "token";

    /// <summary>What a Dhan link asks for: the client id and a token the user generated, never a password or a PIN.</summary>
    public static readonly IReadOnlyList<PortfolioTokenField> DhanFields =
    [
        new("clientId", "Dhan client ID",
            "The number on your Dhan profile (web.dhan.co → My Profile)."),
        new("accessToken", "Access token",
            "Generate it on web.dhan.co → My Profile → Access DhanHQ APIs → Access Token, and paste it here. It lasts 24 hours. " +
            "OpenFNO uses it only to read your holdings, positions and funds. Never paste your password, PIN or TOTP."),
    ];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public static string ConnectMode(string broker) => broker == PortfolioBrokers.Dhan ? ModeToken : ModeRedirect;

    /// <summary>Every broker, linked or not.</summary>
    public async Task<PortfolioConnectionsResponse> ListAsync(long userId, CancellationToken cancellationToken)
    {
        var rows = await db.PortfolioConnections.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        return new PortfolioConnectionsResponse(PortfolioBrokers.All
            .Select(b => View(b, rows.FirstOrDefault(r => r.Broker == b), now))
            .ToList());
    }

    /// <summary>The first step of a link: the broker's sign-in page, or the fields to paste.</summary>
    public async Task<PortfolioStartOutcome> StartAsync(long userId, string broker, CancellationToken cancellationToken)
    {
        switch (broker)
        {
            case PortfolioBrokers.Dhan:
                return new PortfolioStartOutcome(new PortfolioStartResponse(ModeToken, Fields: DhanFields), null, null);

            case PortfolioBrokers.Fyers:
            {
                var app = await fyers.AppAsync(cancellationToken);
                if (app is null) return new PortfolioStartOutcome(null, null, "FYERS linking is not set up on this server yet.");
                string state = states.Issue(userId, broker);
                return new PortfolioStartOutcome(new PortfolioStartResponse(ModeRedirect, FyersPortfolioClient.AuthorizeUrl(app, state)), state, null);
            }

            case PortfolioBrokers.Angel:
            {
                if (angel.ApiKey is not string key) return new PortfolioStartOutcome(null, null, "Angel One linking is not set up on this server yet.");
                string state = states.Issue(userId, broker);
                return new PortfolioStartOutcome(new PortfolioStartResponse(ModeRedirect, AngelPortfolioClient.LoginUrl(key, state)), state, null);
            }

            default:
                return new PortfolioStartOutcome(null, null, $"Unknown broker \"{broker}\".");
        }
    }

    /// <summary>
    /// Finishes a redirect link: the state names the user (and must match this browser's cookie), the broker's code or
    /// token becomes the user's token, one read names the account, and the link is saved.
    /// </summary>
    public async Task<PortfolioLinkOutcome> CompleteRedirectAsync(
        string broker, PortfolioRedirect redirect, string? cookieState, CancellationToken cancellationToken)
    {
        if (ConnectMode(broker) != ModeRedirect) return new PortfolioLinkOutcome(false, null, "This broker does not sign in by redirect.");

        var bound = states.Consume(broker, redirect.State, cookieState, brokerEchoesState: broker == PortfolioBrokers.Fyers);
        if (bound.UserId is not long userId) return new PortfolioLinkOutcome(false, null, bound.Refusal!);

        string name = PortfolioBrokers.NameOf(broker);
        PortfolioToken token;
        try
        {
            if (broker == PortfolioBrokers.Fyers)
            {
                if (string.Equals(redirect.Status, "error", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(redirect.AuthCode))
                {
                    return new PortfolioLinkOutcome(false, userId, "The FYERS sign-in was cancelled or did not finish.");
                }

                token = await fyers.ExchangeAsync(redirect.AuthCode, cancellationToken);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(redirect.AuthToken)) return new PortfolioLinkOutcome(false, userId, "The Angel One sign-in was cancelled or did not finish.");
                token = angel.FromRedirect(redirect.AuthToken);
            }

            var client = Client(broker);
            string account = await client.AccountIdAsync(token, cancellationToken);
            await SaveAsync(userId, broker, token, account, client, cancellationToken);
            return new PortfolioLinkOutcome(true, userId, $"{name} connected ({PortfolioText.MaskAccount(account)}).");
        }
        catch (PortfolioBrokerException ex)
        {
            return new PortfolioLinkOutcome(false, userId, ex.Message);
        }
    }

    /// <summary>
    /// A pasted token (Dhan): checked by one read before anything is saved. The account the token opens must be the
    /// client id the user typed, so a token copied from another account is refused rather than linked.
    /// </summary>
    public async Task<(PortfolioConnectionView? View, string? Error)> SaveTokenAsync(
        long userId, string broker, IReadOnlyDictionary<string, string>? fields, CancellationToken cancellationToken)
    {
        if (ConnectMode(broker) != ModeToken) return (null, $"{PortfolioBrokers.NameOf(broker)} links by signing in at {PortfolioBrokers.NameOf(broker)}, not with a pasted token.");

        string clientId = Field(fields, "clientId");
        string accessToken = Field(fields, "accessToken");
        if (!ClientIdShape().IsMatch(clientId)) return (null, "Enter your Dhan client ID: letters and digits, as on your Dhan profile.");
        if (accessToken.Length is < 20 or > 4096 || accessToken.Any(char.IsWhiteSpace))
        {
            return (null, "Paste the access token generated on web.dhan.co, in one piece.");
        }

        var token = new PortfolioToken(accessToken, ClientId: clientId);
        string account;
        try
        {
            account = await dhan.AccountIdAsync(token, cancellationToken);
        }
        catch (PortfolioBrokerException ex)
        {
            return (null, ex.Message);
        }

        if (!string.Equals(account, clientId, StringComparison.OrdinalIgnoreCase))
        {
            return (null, $"This token opens Dhan account {PortfolioText.MaskAccount(account)}, not {PortfolioText.MaskAccount(clientId)}. Nothing was saved.");
        }

        var row = await SaveAsync(userId, broker, token, account, dhan, cancellationToken);
        return (View(broker, row, _time.GetUtcNow().UtcDateTime), null);
    }

    /// <summary>Deletes the link, and with it the token; <paramref name="forget"/> deletes that broker's snapshots too.</summary>
    public async Task<bool> DisconnectAsync(long userId, string broker, bool forget, CancellationToken cancellationToken)
    {
        var row = await db.PortfolioConnections.FirstOrDefaultAsync(c => c.UserId == userId && c.Broker == broker, cancellationToken);
        if (row is not null) db.PortfolioConnections.Remove(row);

        if (forget)
        {
            var mine = await db.PortfolioSnapshots.Where(s => s.UserId == userId).ToListAsync(cancellationToken);
            db.PortfolioSnapshots.RemoveRange(mine.Where(s => s.Broker == broker));

            // A summary cached on another broker's snapshot may name this broker's holdings: it goes too, and the next
            // sync writes one from what is left.
            foreach (var other in mine.Where(s => s.Broker != broker && s.SummaryJson is not null))
            {
                other.SummaryJson = null;
                other.SummaryKey = null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Portfolio: user {UserId} disconnected {Broker}{Forget}", userId, broker, forget ? " and forgot its snapshots" : string.Empty);
        return row is not null;
    }

    /// <summary>For an admin: how many users linked each broker. No amounts, no holdings, no names.</summary>
    public async Task<IReadOnlyList<PortfolioBrokerCount>> CountsAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var rows = await db.PortfolioConnections.AsNoTracking()
            .Select(c => new PortfolioConnection { Broker = c.Broker, Status = c.Status, ExpiresUtc = c.ExpiresUtc })
            .ToListAsync(cancellationToken);
        return PortfolioBrokers.All.Select(b =>
        {
            var statuses = rows.Where(r => r.Broker == b).Select(r => StatusOf(r, now)).ToList();
            return new PortfolioBrokerCount(b,
                statuses.Count(s => s == PortfolioConnectionStatus.Connected),
                statuses.Count(s => s == PortfolioConnectionStatus.Expired),
                statuses.Count(s => s == PortfolioConnectionStatus.Error));
        }).ToList();
    }

    /// <summary>
    /// A row in the activity log for what happened outside an audited request: the broker's redirect is a GET, which
    /// the activity log middleware does not record. No amounts and no token, as with every row.
    /// </summary>
    public async Task RecordAsync(long userId, string action, string summary, bool succeeded, string path, string? ip, CancellationToken cancellationToken)
    {
        var user = await db.AppUsers.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.UserName, u.Role }).FirstOrDefaultAsync(cancellationToken);
        db.ActivityLog.Add(new ActivityLogEntry
        {
            OccurredUtc = _time.GetUtcNow().UtcDateTime,
            UserId = userId,
            UserName = user?.UserName ?? userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Role = user?.Role ?? string.Empty,
            Module = "portfolio",
            Action = action,
            Method = "GET",
            Path = path.Length <= 300 ? path : path[..300],
            StatusCode = 302,
            Succeeded = succeeded,
            TargetType = "portfolio",
            Summary = summary.Length <= 500 ? summary : summary[..500],
            IpAddress = ip,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public static string StatusOf(PortfolioConnection row, DateTime nowUtc) =>
        row.Status == PortfolioConnectionStatus.Connected && row.ExpiresUtc is DateTime end && end <= nowUtc
            ? PortfolioConnectionStatus.Expired
            : row.Status;

    public static PortfolioConnectionView View(string broker, PortfolioConnection? row, DateTime nowUtc) => new(
        broker,
        PortfolioBrokers.NameOf(broker),
        row is null ? PortfolioConnectionStatus.NotConnected : StatusOf(row, nowUtc),
        row?.AccountLabel,
        row?.ConnectedUtc,
        row?.ExpiresUtc,
        row?.LastSyncUtc,
        row?.LastError,
        ConnectMode(broker),
        broker == PortfolioBrokers.Dhan ? DhanFields : []);

    private IPortfolioBroker Client(string broker) => broker switch
    {
        PortfolioBrokers.Fyers => fyers,
        PortfolioBrokers.Dhan => dhan,
        PortfolioBrokers.Angel => angel,
        _ => throw new ArgumentOutOfRangeException(nameof(broker), broker, "Unknown broker."),
    };

    private async Task<PortfolioConnection> SaveAsync(
        long userId, string broker, PortfolioToken token, string account, IPortfolioBroker client, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var row = await db.PortfolioConnections.FirstOrDefaultAsync(c => c.UserId == userId && c.Broker == broker, cancellationToken);
        if (row is null)
        {
            row = new PortfolioConnection { UserId = userId, Broker = broker };
            db.PortfolioConnections.Add(row);
        }

        row.TokenProtected = vault.Protect(token);
        row.AccountLabel = PortfolioText.MaskAccount(account);
        row.Status = PortfolioConnectionStatus.Connected;
        row.ConnectedUtc = now;
        row.ExpiresUtc = client.ExpiresUtc(token, now);
        row.LastError = null;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Portfolio: user {UserId} connected {Broker} ({Account}), valid until {Expires:u}",
            userId, broker, row.AccountLabel, row.ExpiresUtc);
        return row;
    }

    private static string Field(IReadOnlyDictionary<string, string>? fields, string key) =>
        fields is not null && fields.TryGetValue(key, out var value) ? (value ?? string.Empty).Trim() : string.Empty;

    [GeneratedRegex("^[A-Za-z0-9]{4,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientIdShape();
}
