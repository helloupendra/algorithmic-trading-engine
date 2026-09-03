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

app.MapControllers();

// Prometheus scrapes this without a bearer token, so it opts out of the fallback
// policy. Keep the port off the public internet.
app.MapMetrics().AllowAnonymous();

// SPA fallback: any non-API route serves the React app's index.html so
// client-side routing works on hard refresh / deep links.
app.MapFallbackToFile("index.html").AllowAnonymous();

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