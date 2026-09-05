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
// The signal alerter process, same shape. It was never registered, so every call
// to /api/Alerts/status, start and stop answered 500.
builder.Services.AddSingleton<AlgoTrading.Api.Services.AlertsSupervisor>();

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
// Alert Subscriber Service for logic engine pub/sub
builder.Services.AddHostedService<AlgoTrading.Api.Services.AlertSubscriberService>();


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
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseHttpMetrics();

app.UseCors(CorsPolicies.WebClient);

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
app.MapFallbackToFile("{*path:regex(^(?!api(/|$)|hubs(/|$)|swagger(/|$)).*$)}", "index.html").AllowAnonymous();

app.MapHub<LiveFeedHub>("/hubs/livefeed").AllowAnonymous();



using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;

    var dbContext = services.GetRequiredService<TradingDbContext>();
    await dbContext.Database.MigrateAsync();

    var seeder = services.GetRequiredService<ReferenceDataSeeder>();
    await seeder.SeedAsync();

    // Runs after seeding so it can promote a seeded account if one matches.
    var adminBootstrapper = services.GetRequiredService<AdminBootstrapper>();
    await adminBootstrapper.EnsureAdminAsync();
    await adminBootstrapper.EnsureServiceAccountAsync();
}


app.Run();