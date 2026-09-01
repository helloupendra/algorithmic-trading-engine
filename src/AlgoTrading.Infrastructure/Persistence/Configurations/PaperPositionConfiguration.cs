// src/AlgoTrading.Infrastructure/Persistence/Configurations/PaperPositionConfiguration.cs
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>
/// Entity Framework Core configuration for the <see cref="PaperPosition"/> entity.
/// Defines the schema for tracking live or simulated portfolio positions.
/// </summary>
public class PaperPositionConfiguration : IEntityTypeConfiguration<PaperPosition>
{
    public void Configure(EntityTypeBuilder<PaperPosition> builder)
    {
        builder.ToTable("paper_positions");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.StrategyName).IsRequired().HasMaxLength(100);
        builder.Property(x => x.GroupId).HasMaxLength(100);
        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Direction).IsRequired().HasMaxLength(20);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(50);

        builder.Property(x => x.AveragePrice).HasColumnType("numeric(18,6)");
        builder.Property(x => x.LastMarkPrice).HasColumnType("numeric(18,6)");
        builder.Property(x => x.RealizedPnl).HasColumnType("numeric(18,6)");
        builder.Property(x => x.UnrealizedPnl).HasColumnType("numeric(18,6)");

        builder.Property(x => x.OpenedUtc).IsRequired();
        builder.Property(x => x.UpdatedUtc).IsRequired();

        builder.HasIndex(x => x.SimulationRunId);
        builder.HasIndex(x => x.GroupId);
        builder.HasIndex(x => x.Status);
    }
}