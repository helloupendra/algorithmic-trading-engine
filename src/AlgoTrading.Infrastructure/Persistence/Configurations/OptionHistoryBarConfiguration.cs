using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

public class OptionHistoryBarConfiguration : IEntityTypeConfiguration<OptionHistoryBar>
{
    public void Configure(EntityTypeBuilder<OptionHistoryBar> builder)
    {
        builder.ToTable("option_history_bars");

        // (Id, BarStartUtc): a TimescaleDB hypertable partitioned by BarStartUtc,
        // and a hypertable's unique indexes must include the partitioning column.
        builder.HasKey(x => new { x.Id, x.BarStartUtc });
        builder.Property(x => x.Id).ValueGeneratedOnAdd();

        builder.Property(x => x.Underlying).HasMaxLength(32).IsRequired();
        builder.Property(x => x.ExpiryFlag).HasMaxLength(8).IsRequired();
        builder.Property(x => x.OptionType).HasMaxLength(2).IsRequired();
        builder.Property(x => x.Resolution).HasMaxLength(8).IsRequired();
        builder.Property(x => x.SourceKey).HasMaxLength(32).IsRequired();

        builder.Property(x => x.Strike).HasPrecision(18, 2);
        builder.Property(x => x.Open).HasPrecision(18, 4);
        builder.Property(x => x.High).HasPrecision(18, 4);
        builder.Property(x => x.Low).HasPrecision(18, 4);
        builder.Property(x => x.Close).HasPrecision(18, 4);
        builder.Property(x => x.ImpliedVolatility).HasPrecision(12, 4);
        builder.Property(x => x.SpotPrice).HasPrecision(18, 4);

        // One row per series and bar, so a re-import inserts nothing twice. Also
        // the index every read uses: one series over a time range.
        builder.HasIndex(x => new { x.Underlying, x.ExpiryFlag, x.ExpiryCode, x.StrikeOffset, x.OptionType, x.Resolution, x.BarStartUtc })
            .IsUnique()
            .HasDatabaseName("ux_option_history_series_time");
    }
}
