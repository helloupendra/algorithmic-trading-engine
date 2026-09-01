using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>
/// Entity Framework Core configuration for the <see cref="SimulationRun"/> entity.
/// Defines the schema for backtesting and paper trading execution parameters.
/// </summary>
public class SimulationRunConfiguration : IEntityTypeConfiguration<SimulationRun>
{
    public void Configure(EntityTypeBuilder<SimulationRun> builder)
    {
        builder.ToTable("simulation_runs");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Mode)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.Symbol)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.Resolution)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(x => x.ReplaySpeed)
            .HasMaxLength(50);

        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.StrategyName)
            .HasMaxLength(100);

        builder.Property(x => x.ParametersJson)
            .HasColumnType("text");

        builder.Property(x => x.LastError)
            .HasColumnType("text");

        builder.Property(x => x.CreatedUtc)
            .IsRequired();


        builder.Property(x => x.InitialCapital)
            .HasColumnType("numeric(18,2)");


        builder.HasIndex(x => x.Symbol);
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.Mode);
        builder.HasIndex(x => x.UserId);
    }
}
