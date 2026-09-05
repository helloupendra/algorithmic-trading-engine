using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="BrokerAccount"/>.</summary>
public class BrokerAccountConfiguration : IEntityTypeConfiguration<BrokerAccount>
{
    public void Configure(EntityTypeBuilder<BrokerAccount> builder)
    {
        builder.ToTable("broker_accounts");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderKey)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(x => x.Label)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.CreatedBy)
            .HasMaxLength(100);

        // One account per provider per owner. Postgres treats NULLs as distinct in
        // a unique index, so the shared platform row (UserId null) is guarded by
        // its own filtered index instead.
        builder.HasIndex(x => new { x.ProviderKey, x.UserId }).IsUnique();

        builder.HasIndex(x => x.ProviderKey)
            .IsUnique()
            .HasFilter("\"UserId\" IS NULL")
            .HasDatabaseName("ix_broker_accounts_shared_provider");

        builder.HasIndex(x => x.UserId);
    }
}
