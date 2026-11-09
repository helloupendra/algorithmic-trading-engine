using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using AlgoTrading.Application.Configuration;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Auth;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Persistence.Migrations;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AlgoTrading.IntegrationTests;

public sealed class AuthSessionTransactionTests : IAsyncLifetime
{
    private const long UserId = 7;
    private const string Password = "session-test-password";
    private readonly string _database = $"auth_session_test_{Guid.NewGuid():N}";
    private string _admin = null!, _connection = null!;
    private readonly Clock _clock = new();
    private readonly JwtOptions _jwt = new() { Issuer = "test", Audience = "test", SecretKey = new string('k', 64) };

    public async Task InitializeAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(PostgresTheoryAttribute.Variable)) { Database = "postgres", Pooling = false };
        _admin = builder.ConnectionString;
        await Admin($"CREATE DATABASE \"{_database}\"");
        builder.Database = _database;
        _connection = builder.ConnectionString;
        try
        {
            await using var db = Open();
            await db.Database.EnsureCreatedAsync();
            var user = new AppUser { Id = UserId, UserName = "trader", Email = "trader@example.invalid" };
            user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, Password);
            db.AppUsers.Add(user);
            await db.SaveChangesAsync();
        }
        catch { await DisposeAsync(); throw; }
    }

    public Task DisposeAsync() => Admin($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)");

    [PostgresFact]
    public async Task Concurrent_refreshes_have_one_winner_and_one_active_child()
    {
        var login = await Login();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var db = Open();
            await gate.Task;
            try { return (Result: await Service(db).RefreshAsync(new() { RefreshToken = login.RefreshToken }), Error: (RefreshTokenException?)null); }
            catch (RefreshTokenException ex) { return (Result: (AuthResponse?)null, Error: ex); }
        }).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(calls);
        var winner = Assert.Single(results, x => x.Result != null).Result!;
        Assert.All(results.Where(x => x.Error != null), x => Assert.Equal("refresh_already_used", x.Error!.Code));
        await using var check = Open();
        var tokens = await check.UserRefreshTokens.ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.Equal(Hash(winner.RefreshToken), Assert.Single(tokens, x => x.RevokedUtc == null).TokenHash);
        Assert.Equal(Hash(winner.RefreshToken), Assert.Single(tokens, x => x.TokenHash == Hash(login.RefreshToken)).ReplacedByTokenHash);
        Assert.Equal(Sid(login), Sid(winner));
    }

    [PostgresFact]
    public async Task Old_token_reuse_after_grace_revokes_its_family_only()
    {
        var first = await Login();
        var other = await Login();
        await using (var db = Open()) await Service(db).RefreshAsync(new() { RefreshToken = first.RefreshToken });
        _clock.Now += AuthService.RefreshRaceGrace;
        await using (var db = Open())
        {
            var ex = await Assert.ThrowsAsync<RefreshTokenException>(() => Service(db).RefreshAsync(new() { RefreshToken = first.RefreshToken }));
            Assert.Equal("refresh_reused", ex.Code);
        }
        Assert.False(await Accepted(first));
        Assert.True(await Accepted(other));
        await using var check = Open();
        Assert.False(await check.UserRefreshTokens.AnyAsync(x => x.SessionId == Sid(first) && x.RevokedUtc == null));
    }

    [PostgresFact]
    public async Task Logout_with_a_rotated_parent_revokes_the_child_without_ending_other_devices()
    {
        var first = await Login();
        var other = await Login();
        AuthResponse child;
        await using (var db = Open()) child = await Service(db).RefreshAsync(new() { RefreshToken = first.RefreshToken });
        await using (var db = Open()) await Service(db).LogoutAsync(new() { RefreshToken = first.RefreshToken }, UserId, Sid(first));
        Assert.False(await Accepted(first));
        Assert.False(await Accepted(child));
        Assert.True(await Accepted(other));
        await using var check = Open();
        Assert.Null((await check.AppUsers.SingleAsync()).TokensValidFromUtc);
        Assert.False(await check.UserRefreshTokens.AnyAsync(x => x.SessionId == Sid(first) && x.RevokedUtc == null));
    }

    [PostgresFact]
    public async Task Concurrent_logout_and_refresh_cannot_resurrect_a_session()
    {
        var login = await Login();
        var other = await Login();
        await Task.WhenAll(Task.Run(async () =>
        {
            await using var db = Open();
            try { await Service(db).RefreshAsync(new() { RefreshToken = login.RefreshToken }); }
            catch (RefreshTokenException) { }
        }), Task.Run(async () =>
        {
            await using var db = Open();
            await Service(db).LogoutAsync(new() { RefreshToken = login.RefreshToken });
        }));
        Assert.False(await Accepted(login));
        Assert.True(await Accepted(other));
        await using var check = Open();
        Assert.False(await check.UserRefreshTokens.AnyAsync(x => x.SessionId == Sid(login) && x.RevokedUtc == null));
    }

    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_or_cancelled_replacement_rolls_back_parent_consumption(bool cancel)
    {
        var login = await Login();
        var fault = new FailReplacement(cancel);
        await using (var db = Open(fault))
            await Assert.ThrowsAnyAsync<Exception>(() => Service(db).RefreshAsync(new() { RefreshToken = login.RefreshToken }));
        Assert.True(fault.Reached);
        await using (var check = Open())
        {
            var token = Assert.Single(await check.UserRefreshTokens.ToListAsync());
            Assert.Null(token.RevokedUtc);
            Assert.Null(token.ReplacedByTokenHash);
        }
        await using var retry = Open();
        Assert.NotNull(await Service(retry).RefreshAsync(new() { RefreshToken = login.RefreshToken }));
    }

    [PostgresFact]
    public async Task Database_constraint_refuses_two_active_tokens_in_one_family()
    {
        var login = await Login();
        await using var db = Open();
        db.UserRefreshTokens.Add(new() { UserId = UserId, SessionId = Sid(login), TokenHash = "extra", ExpiresUtc = DateTime.UtcNow.AddDays(1) });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [PostgresFact]
    public async Task Account_wide_revocation_and_refresh_leave_every_old_family_ended()
    {
        var first = await Login();
        var second = await Login();
        await Task.WhenAll(Task.Run(async () =>
        {
            await using var db = Open();
            try { await Service(db).RefreshAsync(new() { RefreshToken = first.RefreshToken }); }
            catch (RefreshTokenException) { }
        }), Task.Run(async () =>
        {
            await using var db = Open();
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var validity = new TokenValidityService(db, cache, NullLogger<TokenValidityService>.Instance);
            await new UserAdminService(db, new PasswordHasher<AppUser>(), validity, NullLogger<UserAdminService>.Instance).RevokeSessionsAsync(UserId);
        }));
        Assert.False(await Accepted(first));
        Assert.False(await Accepted(second));
        await using var check = Open();
        Assert.False(await check.UserRefreshTokens.AnyAsync(x => x.RevokedUtc == null));
        Assert.False(await check.UserSessions.AnyAsync(x => x.RevokedUtc == null));
    }

    [PostgresFact]
    public async Task Logout_rejects_an_authenticated_callers_other_session()
    {
        var first = await Login();
        var other = await Login();
        await using var db = Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).LogoutAsync(new() { RefreshToken = other.RefreshToken }, UserId, Sid(first)));
        Assert.True(await Accepted(first));
        Assert.True(await Accepted(other));
    }

    [PostgresFact]
    public async Task An_expired_session_and_a_legacy_refresh_cannot_issue_new_access()
    {
        var login = await Login();
        await using (var db = Open())
        {
            (await db.UserSessions.SingleAsync()).ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);
            db.UserRefreshTokens.Add(new() { UserId = UserId, TokenHash = Hash("legacy"), ExpiresUtc = DateTime.UtcNow.AddDays(1) });
            await db.SaveChangesAsync();
        }
        await using var check = Open();
        await Assert.ThrowsAsync<RefreshTokenException>(() => Service(check).RefreshAsync(new() { RefreshToken = login.RefreshToken }));
        await Assert.ThrowsAsync<RefreshTokenException>(() => Service(check).RefreshAsync(new() { RefreshToken = "legacy" }));
        Assert.False(await Accepted(login));
        Assert.Single(await check.UserSessions.ToListAsync());
    }

    [PostgresFact]
    public async Task Session_migration_and_rollback_do_not_resurrect_credentials()
    {
        await using var db = Open();
        // This test owns this disposable database. Build just the predecessor tables touched by the migration.
        await db.Database.ExecuteSqlRawAsync("""
            DROP SCHEMA public CASCADE; CREATE SCHEMA public;
            CREATE TABLE app_user ("Id" bigint PRIMARY KEY, "TokensValidFromUtc" timestamptz);
            CREATE TABLE user_refresh_tokens ("Id" bigint PRIMARY KEY, "UserId" bigint, "RevokedUtc" timestamptz);
            INSERT INTO app_user VALUES (7, NULL);
            INSERT INTO user_refresh_tokens VALUES (1, 7, NULL);
            """);
        var migration = new UserLoginSessions();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand("SELECT COUNT(*) FROM user_refresh_tokens WHERE \"RevokedUtc\" IS NULL", connection))
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        var sid = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO user_sessions (\"Id\", \"UserId\", \"CreatedUtc\", \"ExpiresUtc\") VALUES ({sid}, 7, NOW(), NOW() + interval '1 day')");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO user_refresh_tokens (\"Id\", \"UserId\", \"SessionId\") VALUES (2, 7, {sid})");
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await using (var command = new NpgsqlCommand("SELECT COUNT(*) FROM user_refresh_tokens WHERE \"RevokedUtc\" IS NULL", connection))
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        await using (var command = new NpgsqlCommand("SELECT \"TokensValidFromUtc\" IS NOT NULL FROM app_user WHERE \"Id\" = 7", connection))
            Assert.Equal(true, await command.ExecuteScalarAsync());
    }

    [PostgresTheory]
    [InlineData("delete")]
    [InlineData("demote")]
    [InlineData("disable")]
    public async Task Concurrent_admin_removal_keeps_one_active_administrator(string operation)
    {
        await using (var db = Open())
        {
            db.AppUsers.AddRange(
                new AppUser { Id = 21, UserName = "admin-one", Email = "one@example.invalid", Role = "Admin" },
                new AppUser { Id = 22, UserName = "admin-two", Email = "two@example.invalid", Role = "Admin" },
                new AppUser { Id = 23, UserName = "inactive", Email = "off@example.invalid", Role = "Admin", IsActive = false });
            await db.SaveChangesAsync();
        }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new[] { 21L, 22L }.Select(async id =>
        {
            await using var db = Open();
            // Both requests may have already loaded the user before acquiring the administration lock.
            var user = await db.AppUsers.SingleAsync(x => x.Id == id);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var admin = new UserAdminService(db, new PasswordHasher<AppUser>(),
                new TokenValidityService(db, cache, NullLogger<TokenValidityService>.Instance), NullLogger<UserAdminService>.Instance);
            await gate.Task;
            try
            {
                if (operation == "delete") await Service(db).DeleteUserByUsernameAsync(user.UserName);
                else await admin.UpdateAsync(id, new() { Role = operation == "demote" ? "Trader" : null,
                    IsActive = operation == "disable" ? false : null }, 99, "operator");
                return true;
            }
            catch (InvalidOperationException ex)
            {
                Assert.Contains("only active admin", ex.Message);
                return false;
            }
        }).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(calls);
        Assert.Single(results, success => success);
        await using var check = Open();
        Assert.Equal(1, await check.AppUsers.CountAsync(x => x.Role == "Admin" && x.IsActive));
    }

    [PostgresFact]
    public async Task Trading_receipts_prevent_legacy_account_deletion()
    {
        await using var db = Open();
        db.ManualOrderReceipts.Add(new() { UserId = UserId, ClientOrderId = Guid.NewGuid(), RequestHash = "history", FillJson = "{}", CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).DeleteUserByUsernameAsync("trader"));
        Assert.Contains("trading history", ex.Message);
        Assert.True(await db.AppUsers.AnyAsync(x => x.Id == UserId));
        Assert.Single(await db.ManualOrderReceipts.ToListAsync());
    }

    [PostgresFact]
    public async Task Receipt_migration_can_upgrade_and_downgrade_an_empty_table()
    {
        await using var db = Open();
        await db.Database.ExecuteSqlRawAsync("DROP TABLE manual_order_receipts");
        var migration = new ManualOrderReceipts();
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var command in generator.Generate(migration.UpOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        db.ManualOrderReceipts.Add(new() { UserId = UserId, ClientOrderId = Guid.NewGuid(), RequestHash = "probe", FillJson = "{}", CreatedUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Assert.Single(await db.ManualOrderReceipts.ToListAsync());
        await db.ManualOrderReceipts.ExecuteDeleteAsync();
        foreach (var command in generator.Generate(migration.DownOperations)) await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await using var connection = new NpgsqlConnection(_connection);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT to_regclass('manual_order_receipts') IS NULL", connection);
        Assert.Equal(true, await query.ExecuteScalarAsync());
    }

    private TradingDbContext Open(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql(_connection);
        if (interceptor != null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }
    private AuthService Service(TradingDbContext db) => new(db, new PasswordHasher<AppUser>(), Options.Create(_jwt), time: _clock);
    private async Task<AuthResponse> Login()
    {
        await using var db = Open();
        return await Service(db).LoginAsync(new() { UserNameOrEmail = "trader", Password = Password });
    }
    private async Task<bool> Accepted(AuthResponse response)
    {
        await using var db = Open();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(response.AccessToken);
        var issued = DateTimeOffset.FromUnixTimeSeconds(long.Parse(jwt.Claims.Single(c => c.Type == "iat").Value)).UtcDateTime;
        return await new TokenValidityService(db, cache, NullLogger<TokenValidityService>.Instance).IsSessionAcceptableAsync(UserId, Sid(response), issued);
    }
    private static Guid Sid(AuthResponse response) => Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(response.AccessToken).Claims.Single(c => c.Type == "sid").Value);
    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    private async Task Admin(string sql)
    {
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FailReplacement(bool cancel) : SaveChangesInterceptor
    {
        public bool Reached;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<UserRefreshToken>().Any(x => x.State == EntityState.Added))
            {
                Reached = true;
                if (cancel) throw new OperationCanceledException("Injected cancellation");
                throw new InvalidOperationException("Injected replacement failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresTheoryAttribute.Variable)))
            Skip = "Set OPENFNO_TEST_POSTGRES to a disposable PostgreSQL test server.";
    }
}
