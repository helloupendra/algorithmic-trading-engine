using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

public class SimulationEquitySnapshotConfiguration : IEntityTypeConfiguration<SimulationEquitySnapshot>
{
    public void Configure(EntityTypeBuilder<SimulationEquitySnapshot> builder)
    {
        builder.ToTable("simulation_equity_snapshots");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.InitialCapital).HasColumnType("numeric(18,2)");
        builder.Property(x => x.UsedCapital).HasColumnType("numeric(18,2)");
        builder.Property(x => x.AvailableCapital).HasColumnType("numeric(18,2)");
        builder.Property(x => x.RealizedPnl).HasColumnType("numeric(18,2)");
        builder.Property(x => x.UnrealizedPnl).HasColumnType("numeric(18,2)");
        builder.Property(x => x.TotalPnl).HasColumnType("numeric(18,2)");
        builder.Property(x => x.CurrentEquity).HasColumnType("numeric(18,2)");

        builder.Property(x => x.SnapshotUtc).IsRequired();

        builder.HasIndex(x => x.SimulationRunId);
        builder.HasIndex(x => x.SnapshotUtc);
    }
}