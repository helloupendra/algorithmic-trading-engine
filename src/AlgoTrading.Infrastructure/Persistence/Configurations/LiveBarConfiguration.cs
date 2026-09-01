
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>
/// Entity Framework Core configuration for the <see cref="LiveBar"/> entity.
/// Maps the real-time aggregated OHLCV data to the database schema.
/// </summary>
public class LiveBarConfiguration : IEntityTypeConfiguration<LiveBar>
{
    public void Configure(EntityTypeBuilder<LiveBar> builder)
    {
        builder.ToTable("live_bars");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Symbol)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.Resolution)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(x => x.Open)
            .HasColumnType("numeric(18,6)");

        builder.Property(x => x.High)
            .HasColumnType("numeric(18,6)");

        builder.Property(x => x.Low)
            .HasColumnType("numeric(18,6)");

        builder.Property(x => x.Close)
            .HasColumnType("numeric(18,6)");

        builder.Property(x => x.BarStartUtc)
            .IsRequired();

        builder.Property(x => x.UpdatedUtc)
            .IsRequired();

        builder.HasIndex(x => new { x.Symbol, x.Resolution, x.BarStartUtc })
            .IsUnique();
    }
}
