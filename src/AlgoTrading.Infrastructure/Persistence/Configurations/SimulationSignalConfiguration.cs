// src/AlgoTrading.Infrastructure/Persistence/Configurations/SimulationSignalConfiguration.cs
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>
/// Entity Framework Core configuration for the <see cref="SimulationSignal"/> entity.
/// Maps raw algorithm-generated signals to the database schema.
/// </summary>
public class SimulationSignalConfiguration : IEntityTypeConfiguration<SimulationSignal>
{
    public void Configure(EntityTypeBuilder<SimulationSignal> builder)
    {
        builder.ToTable("simulation_signals");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.StrategyName).IsRequired().HasMaxLength(100);
        builder.Property(x => x.SignalType).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Symbol).HasMaxLength(100);
        builder.Property(x => x.GroupId).HasMaxLength(100);
        builder.Property(x => x.MetadataJson).HasColumnType("text");

        builder.Property(x => x.Price).HasColumnType("numeric(18,6)");

        builder.Property(x => x.TimestampUtc).IsRequired();
        builder.Property(x => x.CreatedUtc).IsRequired();

        builder.HasIndex(x => x.SimulationRunId);
        builder.HasIndex(x => x.TimestampUtc);
    }
}