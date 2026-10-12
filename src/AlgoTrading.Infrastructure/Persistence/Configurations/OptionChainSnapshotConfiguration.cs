using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

public class OptionChainSnapshotConfiguration : IEntityTypeConfiguration<OptionChainSnapshot>
{
    public void Configure(EntityTypeBuilder<OptionChainSnapshot> builder)
    {
        builder.ToTable("option_chain_snapshots");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Underlying).HasMaxLength(32).IsRequired();
        builder.Property(x => x.OptionType).HasMaxLength(2).IsRequired();
        builder.Property(x => x.Symbol).HasMaxLength(64).IsRequired();
        builder.Property(x => x.SourceKey).HasMaxLength(32);
        builder.Property(x => x.StrikePrice).HasPrecision(18, 4);
        builder.Property(x => x.SpotPrice).HasPrecision(18, 4);

        foreach (var name in new[] { nameof(OptionChainSnapshot.LastTradedPrice), nameof(OptionChainSnapshot.BidPrice), nameof(OptionChainSnapshot.AskPrice) })
            builder.Property(name).HasPrecision(18, 4);

        foreach (var name in new[] { nameof(OptionChainSnapshot.ImpliedVolatility), nameof(OptionChainSnapshot.Delta), nameof(OptionChainSnapshot.Gamma), nameof(OptionChainSnapshot.Theta), nameof(OptionChainSnapshot.Vega) })
            builder.Property(name).HasPrecision(18, 6);

        // The chain: every strike of one expiry as at a moment. Ordered by time
        // descending because every read wants the newest.
        builder.HasIndex(x => new { x.Underlying, x.ExpiryDate, x.CapturedUtc })
            .HasDatabaseName("ix_chain_underlying_expiry_time");

        // One strike's day: the intraday OI-change curves, and the only query
        // shape the per-strike charts ever make.
        //
        // Unique as well as indexed. A poll stamps every strike with the same
        // CapturedUtc, so a retry — or a second poller started by accident —
        // would otherwise double every row and quietly double the OI a chart
        // reports.
        builder.HasIndex(x => new { x.Symbol, x.CapturedUtc })
            .IsUnique()
            .HasDatabaseName("ux_chain_symbol_time");

        // The newest capture of all, which the poller's status reads every 15 s on two console pages. Without it that
        // read scanned all 29 million rows (40 s each) and kept the 2-CPU server overloaded (12 Oct 2026).
        builder.HasIndex(x => x.CapturedUtc)
            .HasDatabaseName("ix_chain_captured_utc");
    }
}
