using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="PortfolioSnapshot"/>.</summary>
public class PortfolioSnapshotConfiguration : IEntityTypeConfiguration<PortfolioSnapshot>
{
    public void Configure(EntityTypeBuilder<PortfolioSnapshot> builder)
    {
        builder.ToTable("portfolio_snapshots");

        builder.HasKey(x => x.Id);

        // The report reads each broker's newest row, the history a user's rows by time, and retention every row by time.
        builder.HasIndex(x => new { x.UserId, x.Broker, x.TakenUtc }, "IX_portfolio_snapshots_UserId_Broker_TakenUtc");
        builder.HasIndex(x => x.TakenUtc, "IX_portfolio_snapshots_TakenUtc");

        builder.Property(x => x.Broker).IsRequired().HasMaxLength(16);

        // Text, not jsonb: the API writes the shape and reads it back whole; nothing queries inside it.
        builder.Property(x => x.HoldingsJson).IsRequired().HasColumnType("text");
        builder.Property(x => x.PositionsJson).IsRequired().HasColumnType("text");
        builder.Property(x => x.FundsJson).IsRequired().HasColumnType("text");
        builder.Property(x => x.SummaryKey).HasMaxLength(200);
        builder.Property(x => x.SummaryJson).HasColumnType("text");
    }
}
