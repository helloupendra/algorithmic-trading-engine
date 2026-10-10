using AlgoTrading.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AlgoTrading.UnitTests;

/// <summary>
/// The API migrates the database at start-up and refuses to start when the model holds a change no migration
/// carries (EF's PendingModelChangesWarning is an error). On 10 Oct 2026 a merge added two report subject types,
/// and so a new check constraint, without a migration: every other test passed, and the API did not come up on
/// the server. This test fails first instead.
/// </summary>
public class MigrationsTests
{
    [Fact]
    public void Every_model_change_has_a_migration()
    {
        using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql("Host=unused").Options);

        Assert.False(db.Database.HasPendingModelChanges(),
            "The model has changes no migration carries: add one with dotnet ef migrations add.");
    }
}
