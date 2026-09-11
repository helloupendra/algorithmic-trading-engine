using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;
using System.Net;
using System.IdentityModel.Tokens.Jwt;
/// <summary>
/// Application entry point. Configures services, routing, Swagger UI, and the DI container.
/// </summary>

using Microsoft.AspNetCore.Authorization;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Configuration;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Prometheus;
using System.Text;
using AlgoTrading.Api.Hubs;



var builder = WebApplication.CreateBuilder(args);

// Local overrides for secrets (broker keys, DB password, JWT signing key).
// Generated from the repo-root .env by scripts/setup.sh|ps1 and git-ignored, so
// real credentials never reach a tracked file. Added last so it wins over both
// appsettings.json and appsettings.{Environment}.json.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);

// Broker secrets, the trading PIN and FYERS tokens are stored encrypted with
// ASP.NET Data Protection. Its default "application discriminator" is the
// content-root PATH, so a payload written on one machine could not be read on
// another even with the same key ring — the first day on the server every
// stored credential decrypted to "The payload was invalid". A fixed name makes
// the payloads portable wherever the key ring travels (scripts/aws/migrate-db.sh
// copies ~/.aspnet/DataProtection-Keys with the database).
builder.Services.AddDataProtection().SetApplicationName("AlgoTrading");

builder.Services.AddControllers();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "AlgoTrading API", Version = "v1" });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Just paste your raw token here (no need to type 'Bearer ' first).",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

builder.Services.AddSignalR();
builder.Services.AddInfrastructure(builder.Configuration);

// Strategy runner plumbing: where Python lives, the catalog it reports, the
// registry of launched processes and the stop path.
builder.Services.AddSingleton<AlgoTrading.Api.Services.PythonEngineLocator>();
builder.Services.AddSingleton<AlgoTrading.Api.Services.StrategyCatalogService>();
builder.Services.AddSingleton<AlgoTrading.Api.Services.StrategyProcessRegistry>();
builder.Services.AddScoped<AlgoTrading.Api.Services.StrategyRunControl>();
// The per-user history of live runs (list rows + per-user rollup).
builder.Services.AddScoped<AlgoTrading.Api.Services.LiveRunHistoryBuilder>();
// The live data ingestor process: launch, durable pid, adoption after a restart.
builder.Services.AddSingleton<AlgoTrading.Api.Services.IngestorSupervisor>();
// One live feed per connector that declares live ticks, FYERS being the
// ingestor above. The registry builds the other vendors' supervisors itself,
// so a new vendor needs no registration here.
builder.Services.AddSingleton<AlgoTrading.Api.Services.FeedSupervisorRegistry>();
builder.Services.AddSingleton<AlgoTrading.Api.Services.ChainPollerSupervisor>();
// The signal alerter process, same shape. It was never registered, so every call
// to /api/Alerts/status, start and stop answered 500.
builder.Services.AddSingleton<AlgoTrading.Api.Services.AlertsSupervisor>();
builder.Services.AddHttpClient(nameof(AlgoTrading.Api.Services.InstrumentMasterService));
builder.Services.AddHttpClient(nameof(AlgoTrading.Api.Controllers.TraderBrokerController), c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<AlgoTrading.Api.Services.InstrumentMasterService>();
builder.Services.AddScoped<AlgoTrading.Api.Services.WatchlistPruneService>();
// The Telegram notifier, same shape. Started with the API rather than by hand:
// it used to be a sidecar, and the day nobody remembered to start it the
// platform ran all day without a single alert and looked perfectly healthy.
builder.Services.AddSingleton<AlgoTrading.Api.Services.NotifierSupervisor>();

// Backtesting: the backtest runner registry and its stop path, the coverage /
// backfill service and the view builders shared with the live runner.
builder.Services.AddSingleton<AlgoTrading.Api.Services.BacktestProcessRegistry>();
builder.Services.AddScoped<AlgoTrading.Api.Services.BacktestRunControl>();
builder.Services.AddScoped<AlgoTrading.Api.Services.BacktestDataService>();
builder.Services.AddScoped<AlgoTrading.Api.Services.PositionViewBuilder>();
builder.Services.AddScoped<AlgoTrading.Api.Services.BacktestRunViewBuilder>();

// Hosted services start sequentially in registration order, and an
// IHostedService's StartAsync runs to completion before the next one starts.
// The reconcilers therefore come FIRST: runs left Running by a previous API
// process are adopted (by stored pid) or closed before the risk guard and the
// market-close service take their first look at the registry. Registered the
// other way round, a restart after 15:30 IST would let MarketHoursService
// sweep an empty registry, mark today's shutdown done and leave the runners
// adopted a moment later trading all evening.
builder.Services.AddHostedService<AlgoTrading.Api.Services.BacktestStartupReconciler>();
builder.Services.AddHostedService<AlgoTrading.Api.Services.LiveRunStartupReconciler>();
// Register the background service that guards active runs against global kill-switches and rate limits
builder.Services.AddHostedService<AlgoTrading.Api.Services.StrategyRiskGuardService>();
// Market Hours Service for automated halt/flatten at 3:15 PM
builder.Services.AddHostedService<AlgoTrading.Api.Services.MarketHoursService>();
builder.Services.AddHostedService<AlgoTrading.Api.Services.NightlyArchiveService>();
builder.Services.AddHostedService<AlgoTrading.Api.Services.MarketPulseSubscriptionService>();
// Alert Subscriber Service for logic engine pub/sub
builder.Services.AddHostedService<AlgoTrading.Api.Services.AlertSubscriberService>();
// Keeps the Telegram notifier running for as long as the API does.
builder.Services.AddHostedService<AlgoTrading.Api.Services.NotifierStartupService>();


builder.Services.Configure<JwtOptions>(
    builder.Configuration.GetSection("Jwt"));

builder.Services.Configure<AlgoTrading.Api.Configuration.StrategyRunnerOptions>(
    builder.Configuration.GetSection(AlgoTrading.Api.Configuration.StrategyRunnerOptions.SectionName));

builder.Services.AddScoped<PasswordHasher<AppUser>>();
builder.Services.AddScoped<IAuthService, AuthService>();

var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
                ?? throw new InvalidOperationException("Jwt configuration is missing.");

var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SecretKey));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,

            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        // A valid signature and an unexpired lifetime are not enough. Disabling an
        // account, resetting its password or signing it out sets a cutoff, and any
        // token issued before it is refused here — otherwise the token already in
        // someone's hands would keep working for up to its full hour.
        options.Events = new JwtBearerEvents
        {
            // A browser cannot put a header on a WebSocket or EventSource, so
            // SignalR sends the bearer token as ?access_token= on the hub
            // path. Without this the upgrade answered 401, every client fell
            // back to long polling, and the live feed was "fast" on paper only.
            // Limited to the hub path: a token in a query string is never
            // accepted for an ordinary API call.
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                long? userId = principal?.GetUserId();

                if (userId is null)
                {
                    context.Fail("The token carries no usable account id.");
                    return;
                }

                // `iat` is seconds since the epoch; a token without one predates
                // this check and is treated as issued at the epoch, so any cutoff
                // refuses it.
                var issuedAtClaim = principal!.FindFirst(JwtRegisteredClaimNames.Iat)?.Value;
                DateTime issuedAtUtc = long.TryParse(issuedAtClaim, out long seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
                    : DateTime.UnixEpoch;

                var validity = context.HttpContext.RequestServices
                    .GetRequiredService<ITokenValidityService>();

                bool acceptable = await validity.IsTokenAcceptableAsync(
                    userId.Value,
                    issuedAtUtc,
                    context.HttpContext.RequestAborted);

                if (!acceptable)
                {
                    context.Fail("This session has been ended. Sign in again.");
                }
            },
        };
    });

// Deny by default. Any endpoint without explicit authorization metadata requires a
// valid token, so a newly added controller is protected the moment it is written
// rather than the moment someone remembers to add [Authorize]. Public endpoints
// (login, register, broker OAuth callback, metrics) opt out with [AllowAnonymous].
// Behind the Cloudflare tunnel every request arrives from localhost; the real
// client is in X-Forwarded-For, which cloudflared sets. Without this a per-IP
// limit below would count the whole internet as one caller, and the audit log
// would record 127.0.0.1 for everyone. Only loopback is trusted to set it.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});

// Sign-in is the one anonymous endpoint that accepts a password, and on a
// public domain it will be guessed at. Ten attempts a minute per address is
// generous for a person and useless for a script; the same window covers
// refresh so a stolen refresh token cannot be replayed in bulk.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.SignIn, context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 6,
                QueueLimit = 0,
            }));
});

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build())
    .AddPolicy(AuthorizationPolicies.AdminOnly, policy =>
        policy.RequireRole(UserRoles.Admin));

// The browser client sends its bearer token from a different origin, so the allowed
// origins are explicit and configurable. AllowAnyOrigin is never used: combined with
// credentialed requests it would let any site on the internet drive this API.
var corsOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? new[] { "http://localhost:5173", "http://localhost:3000" };

builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicies.WebClient, policy =>
    {
        policy.WithOrigins(corsOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exceptionHandlerPathFeature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerPathFeature>();
        if (exceptionHandlerPathFeature?.Error is AlgoTrading.Application.Exceptions.RiskViolationException riskEx)
        {
            context.Response.StatusCode = 409;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = riskEx.Message });
        }
        else if (exceptionHandlerPathFeature?.Error != null)
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            if (app.Environment.IsDevelopment())
            {
                await context.Response.WriteAsJsonAsync(new 
                { 
                    error = exceptionHandlerPathFeature.Error.Message,
                    stackTrace = exceptionHandlerPathFeature.Error.StackTrace 
                });
            }
            else
            {
                await context.Response.WriteAsJsonAsync(new { error = "An unexpected error occurred." });
            }
        }
    });
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();

    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "AlgoTrading.Api v1");
    });
}

// Serves the built web client from wwwroot so the API and frontend share one
// origin (and one tunnel URL). Populated by `npm run build` — see scripts/go-live.sh.
// Forwarded headers come first so the HTTPS check below sees the scheme the
// tunnel forwarded, not the plain HTTP hop between cloudflared and Kestrel.
app.UseForwardedHeaders();

// Security headers on every response — the console and the API share one
// origin, so one policy covers both. The content-security policy is built
// for what the SPA actually loads: its own bundles, fonts and images, inline
// styles (React and Excalidraw set style attributes), blob workers
// (Excalidraw), same-origin fetch and websockets (SignalR). Nothing is
// loaded from a third party, so nothing else is allowed — a script injected
// through any future XSS cannot run, and no other site may frame the console.
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    h["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: blob:; " +
        // Excalidraw registers a CDN (esm.sh) fallback for every font family
        // it knows; the families the board uses ship in wwwroot/excalidraw,
        // so the fallback is refused here on purpose — the whiteboard's
        // console shows the refusals, and the 12 MB Chinese handwriting
        // family it would otherwise fetch never leaves the CDN.
        "font-src 'self' data:; " +
        "connect-src 'self' ws: wss:; " +
        "worker-src 'self' blob:; " +
        "manifest-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";
    if (context.Request.IsHttps)
    {
        // A year, subdomains included: the console is only ever served over
        // Cloudflare's TLS, so a browser may refuse plain HTTP outright.
        h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }
    await next();
});

app.Use(async (context, next) =>
{
    var p = context.Request.Path.Value ?? string.Empty;
    // /docs and /docs/<page> → their trailing-slash form (a directory with an index.html).
    if ((p == "/docs" || (p.StartsWith("/docs/", StringComparison.Ordinal) && !p.EndsWith('/') && !Path.HasExtension(p)))
        && Directory.Exists(Path.Combine(app.Environment.WebRootPath ?? string.Empty, p.TrimStart('/'))))
    {
        context.Response.Redirect(p + "/" + context.Request.QueryString, permanent: true);
        return;
    }
    // The console is behind a sign-in and is not a page to index; the landing
    // page and the docs are the public face.
    if (p.StartsWith("/admin", StringComparison.Ordinal) || p.StartsWith("/trader", StringComparison.Ordinal)
        || p.StartsWith("/login", StringComparison.Ordinal) || p.StartsWith("/invite", StringComparison.Ordinal)
        || p.StartsWith("/api", StringComparison.Ordinal) || p.StartsWith("/hubs", StringComparison.Ordinal))
    {
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseHttpMetrics();

app.UseCors(CorsPolicies.WebClient);

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// After authentication, so every entry knows who made the request; before the
// endpoints, so it sees the status they return. Reads are skipped and bodies are
// never stored — see ActivityLogMiddleware.
app.UseMiddleware<AlgoTrading.Api.Services.ActivityLogMiddleware>();

app.MapControllers();

// Prometheus scrapes this without a bearer token, so it opts out of the fallback
// policy. Keep the port off the public internet.
app.MapMetrics().AllowAnonymous();

// SPA fallback: any non-API route serves the React app's index.html so
// client-side routing works on hard refresh / deep links. API and hub paths
// are excluded on purpose: an unknown /api route must answer 404, never a
// cacheable HTML document that a client then mistakes for JSON.
// The documentation site under /docs is static HTML of its own; the SPA
// fallback must not swallow it. A docs address without its trailing slash
// (/docs, /docs/architecture) is sent to the canonical one so every page has
// exactly one URL for search engines and for people.
app.MapFallbackToFile("{*path:regex(^(?!api(/|$)|hubs(/|$)|swagger(/|$)|docs(/|$)).*$)}", "index.html").AllowAnonymous();

// Signed-in callers only. Anonymous, this hub shipped the broker's full raw
// tick payload to anyone who had the URL — including every browser sitting on
// the public landing and login pages, which mount the client outside the
// authenticated part of the app.
app.MapHub<LiveFeedHub>("/hubs/livefeed").RequireAuthorization();



using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;

    var dbContext = services.GetRequiredService<TradingDbContext>();
    // Migrations get a long command timeout: a rebuild of a large table's key
    // (live_ticks at 7.6M rows took 33 s for the key alone) is killed at the
    // default 30 s and rolled back. This context is used for the migration
    // only; request-scoped contexts keep the default.
    dbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));
    // Migrations get a long command timeout: a rebuild of a large table's key
    // (live_ticks at 7.6M rows took 33 s for the key alone) is killed at the
    // default 30 s and rolled back. This context is used for the migration
    // only; request-scoped contexts keep the default.
    dbContext.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));
    await dbContext.Database.MigrateAsync();

    var seeder = services.GetRequiredService<ReferenceDataSeeder>();
    await seeder.SeedAsync();

    // Runs after seeding so it can promote a seeded account if one matches.
    var adminBootstrapper = services.GetRequiredService<AdminBootstrapper>();
    await adminBootstrapper.EnsureAdminAsync();
    await adminBootstrapper.EnsureServiceAccountAsync();
}


app.Run();