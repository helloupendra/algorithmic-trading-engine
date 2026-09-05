using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="DataVendor"/>.</summary>
public class DataVendorConfiguration : IEntityTypeConfiguration<DataVendor>
{
    public void Configure(EntityTypeBuilder<DataVendor> builder)
    {
        builder.ToTable("data_vendors");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Key)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(x => x.DisplayName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.Kind)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(32);

        builder.Property(x => x.Directory)
            .IsRequired()
            .HasMaxLength(500);

        builder.Property(x => x.Notes).HasMaxLength(500);
        builder.Property(x => x.CreatedBy).HasMaxLength(100);

        // The key is the SourceKey of every row this vendor writes, so it must be
        // unique across the whole platform — including against shipped adapters,
        // which the API checks before inserting.
        builder.HasIndex(x => x.Key).IsUnique();
    }
}
