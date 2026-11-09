// Test-only HTTP host: real authentication, authorization, manual-order controller and PostgreSQL.
// No broker credentials, market runners or outbound hosted services are registered.
using System.Text;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Hubs;
using AlgoTrading.Api.Security;
using AlgoTrading.Api.Services;
using AlgoTrading.Application.Configuration;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Application.UseCases.LiveData;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Config;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var configured = Environment.GetEnvironmentVariable("OPENFNO_TEST_POSTGRES")
    ?? throw new InvalidOperationException("OPENFNO_TEST_POSTGRES must identify a disposable test server.");
var admin = new NpgsqlConnectionStringBuilder(configured) { Database = "postgres", Pooling = false };
var database = $"browser_test_{Guid.NewGuid():N}";
async Task Admin(string sql) {
    await using var connection = new NpgsqlConnection(admin.ConnectionString);
    await connection.OpenAsync();
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync();
}
await Admin($"CREATE DATABASE \"{database}\"");
var test = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = database };
try {
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls("http://127.0.0.1:5025");
    var jwt = new JwtOptions { Issuer = "browser-test", Audience = "browser-test", SecretKey = new string('b', 64) };
    builder.Services.AddDbContext<TradingDbContext>(o => o.UseNpgsql(test.ConnectionString));
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton(Options.Create(jwt));
    builder.Services.AddSingleton<PasswordHasher<AppUser>>();
    builder.Services.AddScoped<IAuthService, AuthService>();
    builder.Services.AddScoped<ITokenValidityService, TokenValidityService>();
    builder.Services.AddScoped<IUserAdminService, UserAdminService>();
    builder.Services.AddSingleton<BrokerOAuthStates>();
    builder.Services.AddLiveFeedHub(builder.Configuration);
    builder.Services.AddSingleton(new LotSizeOptions());
    builder.Services.AddScoped<ILotSizeResolver, LotSizeResolver>();
    builder.Services.AddScoped<IRiskManagementService, AllowPaperOrders>();
    builder.Services.AddScoped<IPaperTradingService, PaperTradingService>();
    builder.Services.AddControllers().AddApplicationPart(typeof(UserAuthController).Assembly).AddControllersAsServices();
    builder.Services.AddTransient(sp => new ManualOrdersController(sp.GetRequiredService<TradingDbContext>(),
        sp.GetRequiredService<IPaperTradingService>(), sp.GetRequiredService<ILotSizeResolver>(),
        new UpsertWatchlistItemUseCase(null!, null!), sp.GetRequiredService<ILogger<ManualOrdersController>>()));
    builder.Services.AddAuthorizationBuilder().AddPlatformPolicies();
    builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter(RateLimitPolicies.SignIn, l => {
        l.PermitLimit = 100; l.Window = TimeSpan.FromMinutes(1);
    }));
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://127.0.0.1:4173").AllowAnyMethod().AllowAnyHeader().AllowCredentials()));
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => {
        o.TokenValidationParameters = new() { ValidIssuer = jwt.Issuer, ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SecretKey)), ClockSkew = TimeSpan.Zero };
        o.Events = new() { OnTokenValidated = SessionAuthentication.ValidateAsync, OnMessageReceived = context => {
            if (context.Request.Path.StartsWithSegments("/hubs")) context.Token = context.Request.Query["access_token"];
            return Task.CompletedTask;
        } };
    });
    var app = builder.Build();
    app.UseRouting(); app.UseCors(); app.UseAuthentication(); app.UseRateLimiter(); app.UseAuthorization();
    // Fresh synthetic prices on every instrument request; no clock/session dependence.
    app.Use(async (context, next) => {
        if (context.Request.Path == "/api/ManualOrders/instrument") {
            var db = context.RequestServices.GetRequiredService<TradingDbContext>();
            await db.LiveQuotesLatest.ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedUtc, DateTime.UtcNow)
                .SetProperty(x => x.ExchangeTimestampUtc, DateTime.UtcNow));
        }
        await next(context);
    });
    app.MapControllers(); app.MapLiveFeedHub();
    app.MapGet("/test/ready", () => "ready").AllowAnonymous();
    app.MapGet("/test/orders", async (TradingDbContext db) => new {
        orders = await db.PaperOrders.CountAsync(), receipts = await db.ManualOrderReceipts.CountAsync(),
        positions = await db.PaperPositions.Select(p => new { p.StopLossPrice, p.TargetPrice }).ToListAsync()
    }).AllowAnonymous();
    using (var scope = app.Services.CreateScope()) {
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        await db.Database.EnsureCreatedAsync();
        foreach (var (id, name, role) in new[] { (1L, "browser-admin", "Admin"), (2L, "browser-viewer", "Trader") }) {
            var user = new AppUser { Id = id, UserName = name, Email = name + "@example.invalid", Role = role };
            user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, "browser-test-password");
            db.AppUsers.Add(user);
        }
        db.Instruments.Add(new() { Symbol = "NSE:TEST-EQ", Exchange = "NSE", Segment = "CM", InstrumentType = "EQ", IsEnabled = true, LotSize = 1, TickSize = 0.05m });
        db.LiveQuotesLatest.Add(new() { Symbol = "NSE:TEST-EQ", LastTradedPrice = 100, BidPrice = 99.5m, AskPrice = 100.5m,
            UpdatedUtc = DateTime.UtcNow, ExchangeTimestampUtc = DateTime.UtcNow, RawPayload = "{}", SourceKey = "test" });
        await db.SaveChangesAsync();
    }
    await app.RunAsync();
}
finally { await Admin($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)"); }

sealed class AllowPaperOrders : IRiskManagementService {
    public Task EvaluateOrderAsync(long run, string symbol, string side, int quantity, bool closing, CancellationToken ct) => Task.CompletedTask;
    public Task<bool> IsKillSwitchActiveAsync(CancellationToken ct) => Task.FromResult(false);
    public Task<KillSwitchState> GetKillSwitchStateAsync(CancellationToken ct) => Task.FromResult(new KillSwitchState());
    public Task ActivateKillSwitchAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task DeactivateKillSwitchAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task ActivateKillSwitchAsync(string? by, string? reason, CancellationToken ct) => throw new NotSupportedException();
    public Task DeactivateKillSwitchAsync(string? by, string? reason, CancellationToken ct) => throw new NotSupportedException();
}
