using System.Diagnostics;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.Providers;
using AlgoTrading.Contracts.Providers;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AlgoTrading.Api.Controllers;

/// <summary>
/// The Connectors module: every data vendor and broker this build ships, what
/// each can do, its credentials and session, and which of them serves which job.
/// </summary>
/// <remarks>
/// Adding a <em>new vendor</em> still means shipping an adapter — a connector is
/// code, not a config row. What this controller makes self-service is everything
/// after that: credentials, connecting, and routing work between connectors.
/// </remarks>
[Authorize(Policy = AuthorizationPolicies.AdminOnly)]
[ApiController]
[Route("api/Providers")]
public class ProvidersController : ControllerBase
{
    private readonly IProviderCatalog _catalog;
    private readonly IProviderRegistry _registry;
    private readonly IProviderRouter _router;
    private readonly IBrokerCredentialsProvider _credentials;
    private readonly IBrokerSessionStore _sessions;
    private readonly TradingDbContext _dbContext;
    private readonly ILogger<ProvidersController> _logger;

    public ProvidersController(
        IProviderCatalog catalog,
        IProviderRegistry registry,
        IProviderRouter router,
        IBrokerCredentialsProvider credentials,
        IBrokerSessionStore sessions,
        TradingDbContext dbContext,
        ILogger<ProvidersController> logger)
    {
        _catalog = catalog;
        _registry = registry;
        _router = router;
        _credentials = credentials;
        _sessions = sessions;
        _dbContext = dbContext;
        _logger = logger;
    }

    private static readonly ProviderCapability[] AllCapabilities =
        Enum.GetValues<ProviderCapability>();

    /// <summary>Every connector, with its capabilities, credentials and session.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ProviderResponse>>> GetProviders(
        CancellationToken cancellationToken)
    {
        var dataKeys = _registry.DataProviderKeys;
        var brokerKeys = _registry.BrokerProviderKeys;

        // Resolve routing once, so each connector can say what it is serving.
        var serving = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var capability in AllCapabilities)
        {
            if (capability == ProviderCapability.Orders) continue;

            var chain = await _router.ResolveDataChainAsync(capability, cancellationToken: cancellationToken);
            if (chain.Count == 0) continue;

            string key = chain[0].Descriptor.Key;
            if (!serving.TryGetValue(key, out var list))
            {
                serving[key] = list = new List<string>();
            }
            list.Add(capability.ToString());
        }

        var result = new List<ProviderResponse>();

        foreach (var descriptor in _catalog.Descriptors)
        {
            var credentials = await _credentials.GetAsync(descriptor.Key, cancellationToken: cancellationToken);
            var session = await _sessions.GetForProviderAsync(descriptor.Key, cancellationToken);

            result.Add(new ProviderResponse
            {
                Key = descriptor.Key,
                DisplayName = descriptor.DisplayName,
                Kind = descriptor.Kind.ToString(),
                Auth = descriptor.Auth.ToString(),
                IsDataProvider = dataKeys.Contains(descriptor.Key, StringComparer.OrdinalIgnoreCase),
                IsBroker = brokerKeys.Contains(descriptor.Key, StringComparer.OrdinalIgnoreCase),
                Capabilities = Map(descriptor.Capabilities),
                Credentials = new ProviderCredentialsResponse
                {
                    Source = credentials.Source,
                    ClientId = credentials.ClientId,
                    RedirectUri = credentials.RedirectUri,
                    HasSecret = !string.IsNullOrWhiteSpace(credentials.SecretKey),
                    UpdatedBy = credentials.UpdatedBy,
                    UpdatedUtc = credentials.UpdatedUtc,
                },
                Session = MapSession(descriptor, session),
                SuggestedRedirectUri = $"{Request.Scheme}://{Request.Host}/api/Auth/callback",
                ServingCapabilities = serving.TryGetValue(descriptor.Key, out var caps)
                    ? caps
                    : Array.Empty<string>(),
            });
        }

        return Ok(result);
    }

    /// <summary>Saves this connector's app credentials for the shared platform account.</summary>
    [HttpPut("{providerKey}/credentials")]
    public async Task<IActionResult> SaveCredentials(
        string providerKey,
        [FromBody] SaveProviderCredentialsRequest request,
        CancellationToken cancellationToken)
    {
        var descriptor = _catalog.Find(providerKey);
        if (descriptor is null)
        {
            return NotFound(new { message = $"No connector is registered under '{providerKey}'." });
        }

        if (string.IsNullOrWhiteSpace(request.ClientId) ||
            string.IsNullOrWhiteSpace(request.SecretKey) ||
            string.IsNullOrWhiteSpace(request.RedirectUri))
        {
            return BadRequest(new { message = "clientId, secretKey and redirectUri are all required." });
        }

        if (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out _))
        {
            return BadRequest(new { message = "redirectUri must be an absolute URL." });
        }

        await _credentials.SaveAsync(
            descriptor.Key,
            request.ClientId,
            request.SecretKey,
            request.RedirectUri,
            User.Identity?.Name ?? "admin",
            cancellationToken: cancellationToken);

        return Ok(new { message = $"{descriptor.DisplayName} credentials saved. You can connect now." });
    }

    /// <summary>The vendor's hosted-login URL for this connector.</summary>
    [HttpGet("{providerKey}/auth-url")]
    public async Task<IActionResult> GetAuthUrl(string providerKey, CancellationToken cancellationToken)
    {
        var descriptor = _catalog.Find(providerKey);
        if (descriptor is null)
        {
            return NotFound(new { message = $"No connector is registered under '{providerKey}'." });
        }

        try
        {
            var broker = _registry.GetBrokerProvider(descriptor.Key);
            return Ok(new { authUrl = await broker.GetAuthUrlAsync("webui", cancellationToken) });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    /// <summary>Drops this connector's session, leaving every other connector connected.</summary>
    [HttpPost("{providerKey}/disconnect")]
    public async Task<IActionResult> Disconnect(string providerKey, CancellationToken cancellationToken)
    {
        var descriptor = _catalog.Find(providerKey);
        if (descriptor is null)
        {
            return NotFound(new { message = $"No connector is registered under '{providerKey}'." });
        }

        await _sessions.ClearAsync(descriptor.Key, cancellationToken);

        return Ok(new { message = $"{descriptor.DisplayName} disconnected." });
    }

    /// <summary>Which connectors serve which capability, in priority order.</summary>
    [HttpGet("bindings")]
    public async Task<ActionResult<IReadOnlyList<ProviderBindingResponse>>> GetBindings(
        CancellationToken cancellationToken)
    {
        var rows = await _dbContext.ProviderBindings
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .OrderBy(x => x.Priority)
            .ToListAsync(cancellationToken);

        var result = new List<ProviderBindingResponse>();

        foreach (var capability in AllCapabilities)
        {
            string name = capability.ToString();

            var configured = rows
                .Where(x => x.Capability == name)
                .Select(x => x.ProviderKey)
                .ToList();

            if (configured.Count > 0)
            {
                result.Add(new ProviderBindingResponse
                {
                    Capability = name,
                    ProviderKeys = configured,
                    IsFallback = false,
                });
                continue;
            }

            // Nothing configured: report what the router would actually pick, so
            // the console never shows an empty row for a job that is being served.
            var chain = capability == ProviderCapability.Orders
                ? new List<string>()
                : (await _router.ResolveDataChainAsync(capability, cancellationToken: cancellationToken))
                    .Select(x => x.Descriptor.Key)
                    .ToList();

            result.Add(new ProviderBindingResponse
            {
                Capability = name,
                ProviderKeys = chain,
                IsFallback = true,
            });
        }

        return Ok(result);
    }

    /// <summary>Sets the failover chain for one capability. An empty list restores the fallback.</summary>
    [HttpPut("bindings")]
    public async Task<IActionResult> SaveBinding(
        [FromBody] SaveProviderBindingRequest request,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ProviderCapability>(request.Capability, ignoreCase: true, out var capability))
        {
            return BadRequest(new
            {
                message = $"Unknown capability '{request.Capability}'. Expected one of: {string.Join(", ", AllCapabilities)}.",
            });
        }

        foreach (var key in request.ProviderKeys)
        {
            var descriptor = _catalog.Find(key);

            if (descriptor is null)
            {
                return BadRequest(new { message = $"No connector is registered under '{key}'." });
            }

            if (!descriptor.Capabilities.Supports(capability))
            {
                return BadRequest(new
                {
                    message = $"{descriptor.DisplayName} does not provide {capability}.",
                });
            }
        }

        string name = capability.ToString();

        var existing = await _dbContext.ProviderBindings
            .Where(x => x.Capability == name)
            .ToListAsync(cancellationToken);

        _dbContext.ProviderBindings.RemoveRange(existing);

        var now = DateTime.UtcNow;
        string updatedBy = User.Identity?.Name ?? "admin";

        for (int priority = 0; priority < request.ProviderKeys.Count; priority++)
        {
            _dbContext.ProviderBindings.Add(new ProviderBinding
            {
                Capability = name,
                Segment = null,
                ProviderKey = request.ProviderKeys[priority],
                Priority = priority,
                IsEnabled = true,
                UpdatedBy = updatedBy,
                CreatedUtc = now,
                UpdatedUtc = now,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Provider binding for {Capability} set to [{Chain}] by {User}.",
            name, string.Join(" → ", request.ProviderKeys), updatedBy);

        return Ok(new
        {
            message = request.ProviderKeys.Count == 0
                ? $"{name} routing cleared — the platform will use whichever connector claims it."
                : $"{name} now routes to {string.Join(" → ", request.ProviderKeys)}.",
        });
    }

    /// <summary>
    /// Asks the connector for a small slice of real data and reports what came
    /// back. This is the difference between "credentials saved" and "it works".
    /// </summary>
    [HttpPost("{providerKey}/test")]
    public async Task<ActionResult<ProviderTestResponse>> Test(
        string providerKey,
        CancellationToken cancellationToken)
    {
        var descriptor = _catalog.Find(providerKey);
        if (descriptor is null)
        {
            return NotFound(new { message = $"No connector is registered under '{providerKey}'." });
        }

        const string probeSymbol = "NSE:NIFTYBANK-INDEX";
        const string probeResolution = "15";

        var response = new ProviderTestResponse
        {
            ProviderKey = descriptor.Key,
            Probe = $"history {probeSymbol} {probeResolution}m, last 7 days",
        };

        if (!descriptor.Capabilities.History)
        {
            response.Ok = false;
            response.Message = $"{descriptor.DisplayName} does not serve history, so there is nothing to probe here.";
            return Ok(response);
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var provider = _registry.GetDataProvider(descriptor.Key);

            var bars = await provider.GetHistoryAsync(
                probeSymbol,
                probeResolution,
                DateTime.UtcNow.AddDays(-7),
                DateTime.UtcNow,
                cancellationToken);

            response.Ok = true;
            response.BarsReturned = bars.Count;
            response.Message = bars.Count > 0
                ? $"Connected — {bars.Count} bars returned, latest {bars[^1].TimestampUtc:yyyy-MM-dd HH:mm} UTC."
                : "Connected, but the vendor returned no bars for the probe window (a long holiday stretch would do this).";
        }
        catch (Exception ex)
        {
            // A failed probe is an answer, not a server fault: the operator needs
            // the vendor's own words to fix it.
            response.Ok = false;
            response.Message = ex.Message;
        }

        response.ElapsedMs = (int)stopwatch.ElapsedMilliseconds;

        return Ok(response);
    }

    private static ProviderCapabilitiesResponse Map(ProviderCapabilities capabilities) => new()
    {
        History = capabilities.History,
        LiveTicks = capabilities.LiveTicks,
        Quotes = capabilities.Quotes,
        OptionChain = capabilities.OptionChain,
        Orders = capabilities.Orders,
        Depth = capabilities.Depth,
        OpenInterest = capabilities.OpenInterest,
        Greeks = capabilities.Greeks,
        MaxStreamSymbols = capabilities.MaxStreamSymbols,
        HistoryMaxDaysPerCall = capabilities.HistoryMaxDaysPerCall,
        RequestsPerMinute = capabilities.RequestsPerMinute,
        Resolutions = capabilities.Resolutions,
        Segments = capabilities.Segments,
    };

    private static ProviderSessionResponse MapSession(ProviderDescriptor descriptor, BrokerSession? session)
    {
        if (session is null || !session.IsAuthenticated)
        {
            return new ProviderSessionResponse
            {
                IsConnected = false,
                NeedsReconnect = descriptor.Auth == ProviderAuthKind.OAuthDaily,
            };
        }

        var savedUtc = session.UpdatedUtc;

        // A daily token is stale once the IST trading day it was issued on has
        // passed; comparing dates in IST is what an operator actually means by
        // "did I connect today?".
        var ist = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var savedIst = TimeZoneInfo.ConvertTimeFromUtc(savedUtc, ist);
        var nowIst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ist);

        return new ProviderSessionResponse
        {
            IsConnected = true,
            ConnectedUtc = savedUtc,
            AgeSeconds = (int)(DateTime.UtcNow - savedUtc).TotalSeconds,
            NeedsReconnect =
                descriptor.Auth == ProviderAuthKind.OAuthDaily &&
                savedIst.Date < nowIst.Date,
        };
    }
}
