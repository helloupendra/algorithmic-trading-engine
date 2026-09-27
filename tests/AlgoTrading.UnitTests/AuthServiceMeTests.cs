using AlgoTrading.Application.Configuration;
using AlgoTrading.Domain.Constants;
using AlgoTrading.Domain.Entities;
using AlgoTrading.Infrastructure.Persistence;
using AlgoTrading.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// GET /api/UserAuth/me carries a trader's module grants, so the console's
/// workspaces show what the API will answer (web/src/lib/modules.ts, accessFor).
/// </summary>
public class AuthServiceMeTests
{
    private static TradingDbContext Db() =>
        new(new DbContextOptionsBuilder<TradingDbContext>().UseInMemoryDatabase($"me-{Guid.NewGuid():N}").Options);

    private static AuthService Service(TradingDbContext db) =>
        new(db, new PasswordHasher<AppUser>(), Options.Create(new JwtOptions()));

    [Fact]
    public async Task A_trader_gets_the_modules_granted_to_them_and_no_others()
    {
        await using var db = Db();
        db.AppUsers.AddRange(
            new AppUser { Id = 7, UserName = "coderforchange", Role = UserRoles.Trader },
            new AppUser { Id = 8, UserName = "other", Role = UserRoles.Trader });
        db.UserModuleGrants.AddRange(
            new UserModuleGrant { UserId = 7, ModuleKey = PlatformModules.Strategies },
            new UserModuleGrant { UserId = 7, ModuleKey = PlatformModules.MarketData },
            new UserModuleGrant { UserId = 8, ModuleKey = PlatformModules.Backtesting });
        await db.SaveChangesAsync();

        var me = await Service(db).GetMeAsync(7);

        Assert.Equal(new[] { PlatformModules.MarketData, PlatformModules.Strategies }, me!.ModuleGrants);
    }

    [Fact]
    public async Task A_trader_with_no_grant_gets_an_empty_list_not_null()
    {
        await using var db = Db();
        db.AppUsers.Add(new AppUser { Id = 7, UserName = "new", Role = UserRoles.Trader });
        await db.SaveChangesAsync();

        var me = await Service(db).GetMeAsync(7);

        // Null would mean "not known", and the console keeps every tab then.
        Assert.NotNull(me!.ModuleGrants);
        Assert.Empty(me.ModuleGrants!);
    }

    [Fact]
    public async Task An_admin_holds_every_module_by_role_so_no_list_is_sent()
    {
        await using var db = Db();
        db.AppUsers.Add(new AppUser { Id = 1, UserName = "admin", Role = UserRoles.Admin });
        await db.SaveChangesAsync();

        var me = await Service(db).GetMeAsync(1);

        Assert.Null(me!.ModuleGrants);
    }
}
