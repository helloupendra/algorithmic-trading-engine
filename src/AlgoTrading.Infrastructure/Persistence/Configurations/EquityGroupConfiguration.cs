// src/AlgoTrading.Infrastructure/Persistence/Configurations/EquityGroupConfiguration.cs
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

public class EquityGroupConfiguration : IEntityTypeConfiguration<EquityGroup>
{
    public void Configure(EntityTypeBuilder<EquityGroup> builder)
    {
        builder.ToTable("equity_groups");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.Exchange)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(x => x.DisplayName)
            .IsRequired()
            .HasMaxLength(150);

        builder.Property(x => x.Description)
            .HasColumnType("text");

        builder.Property(x => x.CreatedUtc).IsRequired();
        builder.Property(x => x.UpdatedUtc).IsRequired();

        builder.HasIndex(x => x.Name)
            .IsUnique();

        builder.HasIndex(x => new { x.Exchange, x.IsEnabled });
    }
}
