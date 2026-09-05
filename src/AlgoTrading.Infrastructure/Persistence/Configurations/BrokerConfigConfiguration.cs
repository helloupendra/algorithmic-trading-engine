using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>
/// Entity Framework Core configuration for the <see cref="BrokerConfig"/> entity.
/// </summary>
public class BrokerConfigConfiguration : IEntityTypeConfiguration<BrokerConfig>
{
    public void Configure(EntityTypeBuilder<BrokerConfig> builder)
    {
        builder.ToTable("broker_configs");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.BrokerName)
            .IsRequired()
            .HasMaxLength(50);

        // One credential set per broker per account. The shared platform account
        // (BrokerAccountId null) is guarded separately, because Postgres treats
        // NULLs as distinct inside a unique index.
        builder.HasIndex(x => new { x.BrokerName, x.BrokerAccountId }).IsUnique();

        builder.HasIndex(x => x.BrokerName)
            .IsUnique()
            .HasFilter("\"BrokerAccountId\" IS NULL")
            .HasDatabaseName("ix_broker_configs_shared_broker");

        builder.Property(x => x.ClientId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.SecretKeyEncrypted)
            .IsRequired();

        builder.Property(x => x.RedirectUri)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(x => x.UpdatedBy)
            .HasMaxLength(100);
    }
}
