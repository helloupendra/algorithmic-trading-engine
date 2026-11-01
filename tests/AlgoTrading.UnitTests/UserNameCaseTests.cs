using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using AlgoTrading.Api.Controllers;
using AlgoTrading.Api.Services.AiTrader;
using AlgoTrading.Application.Configuration;
using AlgoTrading.Application.Interfaces;
using AlgoTrading.Contracts.Auth;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// Two accounts never differ only in the case of their names, and no check
/// that decides who may do what treats two such names as one person.
/// </summary>
/// <remarks>
/// Until 2 Nov names were unique only case-sensitively (the column's index),
/// while run control and the admin bootstrap compared them in any case: an
/// invitee who accepted as "ADMIN" could stop, flatten and re-rule every run
/// the morning job started as "admin", and the bootstrap, which runs on every
/// start, could find their row first and make it Admin. No migration: the
/// rule is enforced where accounts are created, and the checks compare exactly.
/// </remarks>
public class UserNameCaseTests
{
    private const string ConfiguredPassword = "configured-password-1";

    private static TradingDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"names-{Guid.NewGuid():N}").Options);

    private static AuthService Auth(TradingDbContext db) =>
        new(db, new PasswordHasher<AppUser>(), Options.Create(new JwtOptions
        {
            SecretKey = new string('k', 64), Issuer = "i", Audience = "a", AccessTokenMinutes = 60, RefreshTokenDays = 7,
        }));

    /// <summary>A row as it may already be in a database: written directly, past today's rules.</summary>
    private static async Task<AppUser> AddUserAsync(TradingDbContext db, string userName, string role, string password = "their-own-password")
    {
        var user = new AppUser { UserName = userName, Email = $"{userName}-{Guid.NewGuid():N}@example.com", Role = role, IsActive = true };
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static bool HasPassword(AppUser user, string password) =>
        new PasswordHasher<AppUser>().VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;

    // ---------------------------------------------------------------- creating accounts

    [Theory]
    [InlineData("ADMIN")]
    [InlineData("Admin")]
    [InlineData(" aDmIn ")]
    public async Task A_name_that_differs_only_in_case_from_an_account_is_taken(string variant)
    {
        await using var db = NewDb();
        var auth = Auth(db);
        await auth.RegisterAsync(new RegisterRequest { UserName = "admin", Email = "owner@example.com", Password = "password-1" });

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => auth.RegisterAsync(
            new RegisterRequest { UserName = variant, Email = "intruder@example.com", Password = "password-2" }));

        Assert.Equal("Username is already taken.", refused.Message);
        Assert.Equal("admin", Assert.Single(await db.AppUsers.ToListAsync()).UserName);
    }

    [Fact]
    public async Task A_different_name_still_registers_and_keeps_its_own_case()
    {
        await using var db = NewDb();
        var auth = Auth(db);
        await auth.RegisterAsync(new RegisterRequest { UserName = "admin", Email = "owner@example.com", Password = "password-1" });

        var created = await auth.RegisterAsync(new RegisterRequest { UserName = "Ravi", Email = "ravi@example.com", Password = "password-2" });

        Assert.Equal("Ravi", created.User.UserName);
        Assert.Equal(2, await db.AppUsers.CountAsync());
    }

    [Fact]
    public async Task An_invitee_cannot_accept_under_a_case_variant_of_an_existing_name()
    {
        await using var db = NewDb();
        var auth = Auth(db);
        await auth.RegisterAsync(new RegisterRequest { UserName = "admin", Email = "owner@example.com", Password = "password-1" });

        const string token = "invite-token-for-the-test";
        db.UserInvites.Add(new UserInvite
        {
            TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))),
            Email = "invitee@example.com",
            SuggestedUserName = "invitee",
            CreatedBy = "admin",
            ExpiresUtc = DateTime.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();

        var controller = new InvitesController(db, auth, RecordingFake<IUserAdminService>.Create(new ConcurrentQueue<string>()), NullLogger<InvitesController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var answer = await controller.Accept(token, new InvitesController.AcceptInviteRequest("ADMIN", "password-2"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(answer);
        Assert.Single(await db.AppUsers.ToListAsync());
        Assert.Null((await db.UserInvites.SingleAsync()).AcceptedUtc);
    }

    // ---------------------------------------------------------------- run control

    private static bool CanStop(long callerId, string callerName, string role, string? startedBy, long ownerUserId)
    {
        var controller = (StrategyController)RuntimeHelpers.GetUninitializedObject(typeof(StrategyController));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, callerId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, callerName),
            new Claim(ClaimTypes.Role, role),
        ], "Test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal } };

        var canStop = typeof(StrategyController).GetMethod("CanStop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (bool)canStop.Invoke(controller, [startedBy, ownerUserId])!;
    }

    [Fact]
    public void A_trader_named_like_the_starter_in_another_case_cannot_control_the_run()
    {
        // A run the morning job (signed in as "admin") started in another account (owner 10).
        Assert.False(CanStop(callerId: 9, callerName: "ADMIN", UserRoles.Trader, startedBy: "admin", ownerUserId: 10));
        Assert.False(CanStop(callerId: 9, callerName: "Admin", UserRoles.Trader, startedBy: "admin", ownerUserId: 10));
    }

    [Fact]
    public void The_owner_the_exact_starter_and_an_admin_still_control_the_run()
    {
        Assert.True(CanStop(callerId: 10, callerName: "coderforchange", UserRoles.Trader, startedBy: "admin", ownerUserId: 10));
        Assert.True(CanStop(callerId: 11, callerName: "ravi", UserRoles.Trader, startedBy: "ravi", ownerUserId: 10));
        Assert.True(CanStop(callerId: 1, callerName: "admin", UserRoles.Admin, startedBy: "someone", ownerUserId: 10));
    }

    // ---------------------------------------------------------------- the admin and Service bootstrap

    private static AdminBootstrapper Bootstrapper(TradingDbContext db, string adminPassword = ConfiguredPassword) =>
        new(db, new PasswordHasher<AppUser>(), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bootstrap:AdminUserName"] = "admin",
            ["Bootstrap:AdminEmail"] = "admin@localhost",
            ["Bootstrap:AdminPassword"] = adminPassword,
            ["Bootstrap:ServiceUserName"] = "engine-service",
            ["Bootstrap:ServicePassword"] = "service-password-1",
        }).Build(), NullLogger<AdminBootstrapper>.Instance);

    [Fact]
    public async Task The_start_up_sync_changes_the_configured_admin_and_never_a_case_variant_beside_it()
    {
        await using var db = NewDb();
        // The impostor's row is older, so an unordered any-case match finds it first.
        var impostor = await AddUserAsync(db, "ADMIN", UserRoles.Trader, "intruder-password");
        var admin = await AddUserAsync(db, "admin", UserRoles.Admin, "old-admin-password");

        await Bootstrapper(db).EnsureAdminAsync();

        await db.Entry(impostor).ReloadAsync();
        await db.Entry(admin).ReloadAsync();
        Assert.Equal(UserRoles.Trader, impostor.Role);
        Assert.True(HasPassword(impostor, "intruder-password"));
        Assert.True(HasPassword(admin, ConfiguredPassword));
    }

    [Fact]
    public async Task A_case_variant_of_the_configured_admin_is_never_promoted_or_shadowed()
    {
        await using var db = NewDb();
        await AddUserAsync(db, "owner", UserRoles.Admin);
        var impostor = await AddUserAsync(db, "ADMIN", UserRoles.Trader, "intruder-password");

        await Bootstrapper(db).EnsureAdminAsync();

        await db.Entry(impostor).ReloadAsync();
        Assert.Equal(UserRoles.Trader, impostor.Role);
        Assert.True(HasPassword(impostor, "intruder-password"));
        Assert.Equal(2, await db.AppUsers.CountAsync());
    }

    [Fact]
    public async Task With_no_admin_at_all_a_case_variant_is_still_not_promoted()
    {
        await using var db = NewDb();
        var impostor = await AddUserAsync(db, "Admin", UserRoles.Trader, "intruder-password");

        await Bootstrapper(db).EnsureAdminAsync();

        await db.Entry(impostor).ReloadAsync();
        Assert.Equal(UserRoles.Trader, impostor.Role);
        Assert.False(await db.AppUsers.AnyAsync(x => x.Role == UserRoles.Admin));
    }

    [Fact]
    public async Task With_no_admin_at_all_the_exact_name_is_still_promoted()
    {
        await using var db = NewDb();
        var seeded = await AddUserAsync(db, "admin", UserRoles.Trader);

        await Bootstrapper(db).EnsureAdminAsync();

        await db.Entry(seeded).ReloadAsync();
        Assert.Equal(UserRoles.Admin, seeded.Role);
    }

    [Fact]
    public async Task The_service_sync_keeps_the_exact_account_and_leaves_a_case_variant_alone()
    {
        await using var db = NewDb();
        var impostor = await AddUserAsync(db, "Engine-Service", UserRoles.Trader, "intruder-password");
        var service = await AddUserAsync(db, "engine-service", UserRoles.Service, "old-service-password");

        await Bootstrapper(db).EnsureServiceAccountAsync();

        await db.Entry(impostor).ReloadAsync();
        await db.Entry(service).ReloadAsync();
        Assert.True(HasPassword(impostor, "intruder-password"));
        Assert.True(HasPassword(service, "service-password-1"));
        Assert.Equal(UserRoles.Trader, impostor.Role);
    }

    [Fact]
    public async Task The_service_sync_creates_no_second_account_beside_a_case_variant()
    {
        await using var db = NewDb();
        var impostor = await AddUserAsync(db, "ENGINE-SERVICE", UserRoles.Trader, "intruder-password");

        await Bootstrapper(db).EnsureServiceAccountAsync();

        await db.Entry(impostor).ReloadAsync();
        Assert.True(HasPassword(impostor, "intruder-password"));
        Assert.Single(await db.AppUsers.ToListAsync());
    }

    // ---------------------------------------------------------------- other lookups by name

    [Fact]
    public async Task Deleting_by_name_deletes_only_the_exact_name()
    {
        await using var db = NewDb();
        await AddUserAsync(db, "ADMIN", UserRoles.Trader);
        await AddUserAsync(db, "admin", UserRoles.Admin);
        var auth = Auth(db);

        Assert.False(await auth.DeleteUserByUsernameAsync("Admin"));
        Assert.True(await auth.DeleteUserByUsernameAsync("ADMIN"));

        Assert.Equal("admin", Assert.Single(await db.AppUsers.ToListAsync()).UserName);
    }

    [Fact]
    public async Task The_ai_traders_account_is_found_by_its_exact_name_only()
    {
        await using var db = NewDb();
        await AddUserAsync(db, "AI-Trader", UserRoles.Trader);

        Assert.Null(await AiTraderAccount.FindAsync(db, CancellationToken.None));

        var own = await AddUserAsync(db, AiTraderAccount.UserName, UserRoles.Trader);
        Assert.Equal(own.Id, (await AiTraderAccount.FindAsync(db, CancellationToken.None))?.Id);
    }
}
