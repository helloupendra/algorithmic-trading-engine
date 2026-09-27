using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="RunPnlMinute"/>.</summary>
public class RunPnlMinuteConfiguration : IEntityTypeConfiguration<RunPnlMinute>
{
    public void Configure(EntityTypeBuilder<RunPnlMinute> builder)
    {
        builder.ToTable("run_pnl_minutes");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.AtUtc).IsRequired();

        builder.Property(x => x.Realized).HasColumnType("numeric(18,2)");
        builder.Property(x => x.Unrealized).HasColumnType("numeric(18,2)");
        builder.Property(x => x.Charges).HasColumnType("numeric(18,2)");
        builder.Property(x => x.Net).HasColumnType("numeric(18,2)");

        // One row per run per minute. The recorder updates a minute it has
        // already written (the run's final row lands on the minute it was last
        // sampled in); the index is what makes a second writer fail loudly
        // instead of doubling a minute into every sum drawn from the table.
        builder.HasIndex(x => new { x.SimulationRunId, x.AtUtc }, "UX_run_pnl_minutes_SimulationRunId_AtUtc")
            .IsUnique();

        // The Desk asks for one IST day across every run in scope.
        builder.HasIndex(x => x.AtUtc, "IX_run_pnl_minutes_AtUtc");
    }
}
