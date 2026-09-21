using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="SimBrokerAccount"/>.</summary>
public class SimBrokerAccountConfiguration : IEntityTypeConfiguration<SimBrokerAccount>
{
    public void Configure(EntityTypeBuilder<SimBrokerAccount> builder)
    {
        builder.ToTable("sim_broker_accounts");

        builder.HasKey(x => x.Id);

        // One trader, one account: issuing a second would leave money in an
        // account nobody can see.
        builder.HasIndex(x => x.UserId).IsUnique();

        // And one trader per broker account, for the same reason read the other
        // way round.
        builder.HasIndex(x => x.ClientId).IsUnique();

        builder.Property(x => x.ClientId).IsRequired().HasMaxLength(32);
        builder.Property(x => x.AppId).IsRequired().HasMaxLength(64);
        builder.Property(x => x.AppSecretProtected).IsRequired();
        builder.Property(x => x.TotpSecretProtected).IsRequired();
        builder.Property(x => x.StaticIps).HasMaxLength(200);
        builder.Property(x => x.CreatedBy).HasMaxLength(100);
    }
}
