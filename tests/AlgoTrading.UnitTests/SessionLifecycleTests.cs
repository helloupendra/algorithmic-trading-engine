using System.Net;
using System.Reflection;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Security;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Users;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static AlgoTrading.UnitTests.DeskWriteAccessTests;

namespace AlgoTrading.UnitTests;

/// <summary>
/// What an account holds ends when the decision is made: a demotion, a disabled
/// account or a changed password takes effect on the next request, and a trader
/// can change their own password at all.
/// </summary>
/// <remarks>
/// Until 2 Nov a demoted admin kept full admin access for the rest of their token's
/// hour, promoting themselves back included, because AdminOnly reads the role from
/// the token; a refresh token stolen before an own password change outlived it for
/// good; and POST api/Users/me/password answered 403 to every trader, the
/// controller's AdminOnly being added to the action's [Authorize].
/// </remarks>
public class SessionLifecycleTests
{
    private const long Victim = 20;
    private const string OldPassword = "old-password-1";
    private const string NewPassword = "new-password-1";

    /// <summary>A token this account was issued a while ago, as its holder would present it.</summary>
    private static readonly DateTime IssuedEarlier = DateTime.UtcNow.AddMinutes(-10);

    /// <summary>The real account service, over the real per-request cutoff.</summary>
    private sealed class Desk : IAsyncDisposable
    {
        private readonly DbContextOptions<TradingDbContext> _options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseInMemoryDatabase($"sessions-{Guid.NewGuid():N}").Options;

        private readonly TradingDbContext _db;
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());

        public Desk(string victimRole)
        {
            _db = new TradingDbContext(_options);
            var hasher = new PasswordHasher<AppUser>();
            AddUser(hasher, AdminId, "admin", UserRoles.Admin);
            AddUser(hasher, Victim, "victim", victimRole);
            _db.UserRefreshTokens.AddRange(
                new UserRefreshToken { UserId = Victim, TokenHash = "laptop", ExpiresUtc = DateTime.UtcNow.AddDays(7) },
                new UserRefreshToken { UserId = Victim, TokenHash = "stolen", ExpiresUtc = DateTime.UtcNow.AddDays(7) },
                new UserRefreshToken { UserId = AdminId, TokenHash = "admin's own", ExpiresUtc = DateTime.UtcNow.AddDays(7) });
            _db.SaveChanges();

            Validity = new TokenValidityService(_db, _cache, NullLogger<TokenValidityService>.Instance);
            Users = new UserAdminService(_db, hasher, Validity, NullLogger<UserAdminService>.Instance, Sockets);
        }

        public TokenValidityService Validity { get; }

        public UserAdminService Users { get; }

        public SocketCloser Sockets { get; } = new();

        private void AddUser(PasswordHasher<AppUser> hasher, long id, string name, string role)
        {
            var user = new AppUser { Id = id, UserName = name, Email = $"{name}@example.com", Role = role, IsActive = true };
            user.PasswordHash = hasher.HashPassword(user, OldPassword);
            _db.AppUsers.Add(user);
        }

        public Task<bool> VictimsTokenAcceptedAsync() => Validity.IsTokenAcceptableAsync(Victim, IssuedEarlier);

        /// <summary>Read past the service's context: what a later request would find stored.</summary>
        public async Task<AppUser> StoredVictimAsync()
        {
            await using var fresh = new TradingDbContext(_options);
            return await fresh.AppUsers.AsNoTracking().SingleAsync(x => x.Id == Victim);
        }

        public async Task<int> LiveRefreshTokensAsync(long userId)
        {
            await using var fresh = new TradingDbContext(_options);
            return await fresh.UserRefreshTokens.CountAsync(x => x.UserId == userId && x.RevokedUtc == null);
        }

        public async Task AssertSignedOutEverywhereAsync()
        {
            Assert.False(await VictimsTokenAcceptedAsync());
            Assert.Equal(0, await LiveRefreshTokensAsync(Victim));
            Assert.Equal(new[] { Victim }, Sockets.ClosedFor);
        }

        public async Task AssertStillSignedInAsync()
        {
            Assert.True(await VictimsTokenAcceptedAsync());
            Assert.Equal(2, await LiveRefreshTokensAsync(Victim));
            Assert.Empty(Sockets.ClosedFor);
        }

        public async ValueTask DisposeAsync()
        {
            await _db.DisposeAsync();
            _cache.Dispose();
        }
    }

    private sealed class SocketCloser : ILiveConnectionCloser
    {
        public List<long> ClosedFor { get; } = new();

        public int CloseConnectionsOf(long userId)
        {
            ClosedFor.Add(userId);
            return 1;
        }
    }

    private static Task UpdateAsync(Desk desk, UpdateUserRequest request) => desk.Users.UpdateAsync(Victim, request, AdminId, "admin");

    private static bool Verifies(AppUser user, string password) =>
        new PasswordHasher<AppUser>().VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;

    // ---------------------------------------------------------------- a change of role

    [Fact]
    public async Task A_demoted_admin_is_signed_out_at_once()
    {
        await using var desk = new Desk(UserRoles.Admin);
        await desk.AssertStillSignedInAsync();

        await UpdateAsync(desk, new UpdateUserRequest { Role = UserRoles.Trader });

        Assert.Equal(UserRoles.Trader, (await desk.StoredVictimAsync()).Role);
        await desk.AssertSignedOutEverywhereAsync();
    }

    [Fact]
    public async Task A_token_issued_after_the_demotion_is_accepted_and_carries_the_new_role()
    {
        await using var desk = new Desk(UserRoles.Admin);

        await UpdateAsync(desk, new UpdateUserRequest { Role = UserRoles.Trader });

        // The cutoff is compared by the second: a sign-in after it works.
        Assert.True(await desk.Validity.IsTokenAcceptableAsync(Victim, DateTime.UtcNow.AddSeconds(2)));
    }

    [Fact]
    public async Task A_promotion_signs_the_account_out_too_so_no_token_holds_a_stale_role()
    {
        await using var desk = new Desk(UserRoles.Trader);

        await UpdateAsync(desk, new UpdateUserRequest { Role = UserRoles.Admin });

        await desk.AssertSignedOutEverywhereAsync();
    }

    [Fact]
    public async Task The_same_role_sent_again_signs_nobody_out()
    {
        await using var desk = new Desk(UserRoles.Admin);

        await UpdateAsync(desk, new UpdateUserRequest { Role = "admin" });

        await desk.AssertStillSignedInAsync();
    }

    [Fact]
    public async Task A_change_that_touches_neither_role_nor_access_signs_nobody_out()
    {
        await using var desk = new Desk(UserRoles.Trader);

        await UpdateAsync(desk, new UpdateUserRequest { TotalCapital = 500_000m, MaxConcurrentRuns = 3 });

        Assert.Equal(500_000m, (await desk.StoredVictimAsync()).TotalCapital);
        await desk.AssertStillSignedInAsync();
    }

    [Fact]
    public async Task A_refused_change_changes_nothing_and_signs_nobody_out()
    {
        await using var desk = new Desk(UserRoles.Admin);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UpdateAsync(desk, new UpdateUserRequest { Role = UserRoles.Trader, IsActive = false, TotalCapital = -1m }));

        var stored = await desk.StoredVictimAsync();
        Assert.Equal(UserRoles.Admin, stored.Role);
        Assert.True(stored.IsActive);
        await desk.AssertStillSignedInAsync();
    }

    [Fact]
    public async Task An_admin_changing_their_own_details_stays_signed_in()
    {
        await using var desk = new Desk(UserRoles.Trader);

        await desk.Users.UpdateAsync(AdminId, new UpdateUserRequest { Role = UserRoles.Admin, TotalCapital = 1m }, AdminId, "admin");

        Assert.True(await desk.Validity.IsTokenAcceptableAsync(AdminId, IssuedEarlier));
        Assert.Equal(1, await desk.LiveRefreshTokensAsync(AdminId));
    }

    [Fact]
    public async Task Disabling_an_account_still_signs_it_out()
    {
        await using var desk = new Desk(UserRoles.Trader);

        await UpdateAsync(desk, new UpdateUserRequest { IsActive = false });

        Assert.False((await desk.StoredVictimAsync()).IsActive);
        await desk.AssertSignedOutEverywhereAsync();
    }

    // ---------------------------------------------------------------- a change of password

    [Fact]
    public async Task Changing_your_own_password_ends_every_session_on_the_old_one()
    {
        await using var desk = new Desk(UserRoles.Trader);

        await desk.Users.ChangeOwnPasswordAsync(Victim, OldPassword, NewPassword);

        var stored = await desk.StoredVictimAsync();
        Assert.True(Verifies(stored, NewPassword));
        Assert.False(Verifies(stored, OldPassword));
        await desk.AssertSignedOutEverywhereAsync();
    }

    [Fact]
    public async Task A_wrong_current_password_changes_nothing_and_ends_nothing()
    {
        await using var desk = new Desk(UserRoles.Trader);

        await Assert.ThrowsAsync<InvalidOperationException>(() => desk.Users.ChangeOwnPasswordAsync(Victim, "not-the-password", NewPassword));

        Assert.True(Verifies(await desk.StoredVictimAsync(), OldPassword));
        await desk.AssertStillSignedInAsync();
    }

    [Fact]
    public async Task An_admins_reset_ends_every_session_on_the_old_password()
    {
        await using var desk = new Desk(UserRoles.Trader);

        await desk.Users.ResetPasswordAsync(Victim, NewPassword, "admin");

        Assert.True(Verifies(await desk.StoredVictimAsync(), NewPassword));
        await desk.AssertSignedOutEverywhereAsync();
    }

    [Fact]
    public async Task Ending_one_accounts_sessions_leaves_the_others_alone()
    {
        await using var desk = new Desk(UserRoles.Trader);

        await desk.Users.ResetPasswordAsync(Victim, NewPassword, "admin");

        Assert.True(await desk.Validity.IsTokenAcceptableAsync(AdminId, IssuedEarlier));
        Assert.Equal(1, await desk.LiveRefreshTokensAsync(AdminId));
    }

    // ---------------------------------------------------------------- through the API's policies

    private const string MyPassword = "/api/Users/me/password";

    private static string Body(string current = OldPassword, string next = NewPassword) =>
        $$"""{"currentPassword":"{{current}}","newPassword":"{{next}}"}""";

    /// <summary>The real controller and account service over a database holding one account of each role.</summary>
    private static async Task<Host> OwnPasswordHost()
    {
        string database = $"own-password-{Guid.NewGuid():N}";
        var host = await Host.StartAsync<OwnPasswordController>(s =>
        {
            s.AddDbContext<TradingDbContext>(o => o.UseInMemoryDatabase(database));
            s.AddMemoryCache();
            s.AddSingleton(new PasswordHasher<AppUser>());
            s.AddScoped<ITokenValidityService, TokenValidityService>();
            s.AddScoped<IUserAdminService, UserAdminService>();
        });

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var hasher = new PasswordHasher<AppUser>();
        foreach (var (id, name, role) in new[] { (AdminId, "admin", UserRoles.Admin), (ServiceId, "engine-service", UserRoles.Service), (TraderId, "ravi", UserRoles.Trader), (9L, "sita", UserRoles.Trader) })
        {
            var user = new AppUser { Id = id, UserName = name, Email = $"{name}@example.com", Role = role, IsActive = true };
            user.PasswordHash = hasher.HashPassword(user, OldPassword);
            db.AppUsers.Add(user);
        }
        db.UserRefreshTokens.Add(new UserRefreshToken { UserId = TraderId, TokenHash = "stolen", ExpiresUtc = DateTime.UtcNow.AddDays(7) });
        await db.SaveChangesAsync();
        return host;
    }

    private static async Task<AppUser> StoredAsync(Host host, long id)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TradingDbContext>().AppUsers.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    [Fact]
    public async Task A_trader_can_change_their_own_password_and_it_ends_their_sessions()
    {
        await using var host = await OwnPasswordHost();

        using var response = await host.ExchangeAsync("POST", MyPassword, Body(), TraderId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("sign in again", await response.Content.ReadAsStringAsync());
        var stored = await StoredAsync(host, TraderId);
        Assert.True(Verifies(stored, NewPassword));
        Assert.NotNull(stored.TokensValidFromUtc);

        using var scope = host.Services.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<TradingDbContext>().UserRefreshTokens.AnyAsync(x => x.UserId == TraderId && x.RevokedUtc == null));
    }

    [Fact]
    public async Task An_admin_still_can_and_the_change_is_only_ever_to_the_callers_own_account()
    {
        await using var host = await OwnPasswordHost();

        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", MyPassword, Body(), AdminId));

        Assert.True(Verifies(await StoredAsync(host, AdminId), NewPassword));
        Assert.True(Verifies(await StoredAsync(host, TraderId), OldPassword));
    }

    [Fact]
    public async Task The_service_account_cannot_change_the_password_every_runner_signs_in_with()
    {
        await using var host = await OwnPasswordHost();

        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync("POST", MyPassword, Body(), ServiceId));
        Assert.True(Verifies(await StoredAsync(host, ServiceId), OldPassword));
    }

    [Fact]
    public async Task Anonymous_is_refused()
    {
        await using var host = await OwnPasswordHost();

        Assert.Equal(HttpStatusCode.Unauthorized, await host.SendAsync("POST", MyPassword, Body(), userId: null));
    }

    [Fact]
    public async Task A_wrong_current_password_is_told_so_and_a_short_new_one_costs_no_attempt()
    {
        await using var host = await OwnPasswordHost();

        Assert.Equal(HttpStatusCode.BadRequest, await host.SendAsync("POST", MyPassword, Body(current: "not-the-password"), TraderId));
        Assert.True(Verifies(await StoredAsync(host, TraderId), OldPassword));

        for (int i = 0; i < OwnPasswordController.MaxAttempts + 1; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, await host.SendAsync("POST", MyPassword, Body(next: "short"), TraderId));
        }

        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", MyPassword, Body(), TraderId));
    }

    [Fact]
    public async Task Guessing_the_current_password_stops_after_the_attempt_limit_per_account()
    {
        await using var host = await OwnPasswordHost();

        for (int i = 0; i < OwnPasswordController.MaxAttempts; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, await host.SendAsync("POST", MyPassword, Body(current: $"guess-{i}"), TraderId));
        }

        // Even the right one: past the limit the answer says nothing about the guess.
        using var refused = await host.ExchangeAsync("POST", MyPassword, Body(), TraderId);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotNull(refused.Headers.RetryAfter);
        Assert.True(Verifies(await StoredAsync(host, TraderId), OldPassword));

        // Someone else's account is its own count.
        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", MyPassword, Body(), 9));
    }

    [Fact]
    public async Task The_correct_password_clears_the_count()
    {
        await using var host = await OwnPasswordHost();

        for (int i = 0; i < OwnPasswordController.MaxAttempts - 1; i++)
        {
            await host.SendAsync("POST", MyPassword, Body(current: $"guess-{i}"), TraderId);
        }
        Assert.Equal(HttpStatusCode.OK, await host.SendAsync("POST", MyPassword, Body(), TraderId));

        for (int i = 0; i < OwnPasswordController.MaxAttempts; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, await host.SendAsync("POST", MyPassword, Body(current: $"guess-{i}", next: "newer-password-1"), TraderId));
        }
    }

    [Fact]
    public void The_address_limit_on_sign_in_covers_it_too()
    {
        var limit = typeof(OwnPasswordController).GetMethod(nameof(OwnPasswordController.Change))!.GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.Equal(RateLimitPolicies.SignIn, limit?.PolicyName);
    }

    public static TheoryData<string, string, string?> AdminRoutes => new()
    {
        { "GET", "/api/Users", null },
        { "GET", $"/api/Users/{TraderId}", null },
        { "PATCH", $"/api/Users/{TraderId}", """{"role":"Admin"}""" },
        { "PUT", $"/api/Users/{TraderId}/grants", """{"moduleKeys":[]}""" },
        { "POST", $"/api/Users/{TraderId}/password", """{"newPassword":"new-password-1"}""" },
        { "POST", $"/api/Users/{TraderId}/revoke-sessions", null },
    };

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task Opening_the_own_password_route_opened_nothing_else_under_api_users(string method, string path, string? body)
    {
        await using var host = await Host.StartAsync<UsersController>(s => s.AddFake<IUserAdminService>());

        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync(method, path, body, TraderId));
        Assert.Equal(HttpStatusCode.Forbidden, await host.SendAsync(method, path, body, ServiceId));
        Assert.Empty(host.Calls);

        var status = await host.SendAsync(method, path, body, AdminId);
        Assert.True(status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden), $"{path} answered {(int)status}");
    }
}
