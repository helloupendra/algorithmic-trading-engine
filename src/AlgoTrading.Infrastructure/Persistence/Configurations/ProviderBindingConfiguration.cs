using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="ProviderBinding"/>.</summary>
public class ProviderBindingConfiguration : IEntityTypeConfiguration<ProviderBinding>
{
    public void Configure(EntityTypeBuilder<ProviderBinding> builder)
    {
        builder.ToTable("provider_bindings");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Capability)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(x => x.Segment)
            .HasMaxLength(16);

        builder.Property(x => x.ProviderKey)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(x => x.UpdatedBy)
            .HasMaxLength(100);

        // A provider appears at most once per (capability, segment); its position
        // in the failover chain is Priority.
        builder.HasIndex(x => new { x.Capability, x.Segment, x.ProviderKey }).IsUnique();
        builder.HasIndex(x => new { x.Capability, x.Priority });
    }
}
