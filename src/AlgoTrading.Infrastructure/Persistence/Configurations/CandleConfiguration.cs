using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>
/// Entity Framework Core configuration for the <see cref="Candle"/> entity.
/// Defines table mapping, column types (especially for high-precision decimals), and a unique composite index.
/// </summary>
public class CandleConfiguration : IEntityTypeConfiguration<Candle>
{
    public void Configure(EntityTypeBuilder<Candle> builder)
    {
        builder.ToTable("candles");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Symbol)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.Resolution)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(x => x.TimeStampUtc)
            .IsRequired();

        builder.Property(x => x.Open)
            .HasColumnType("numeric(18,6)");

        builder.Property(x => x.High)
            .HasColumnType("numeric(18,6)");

        builder.Property(x => x.Low)
            .HasColumnType("numeric(18,6)");

        builder.Property(x => x.Close)
            .HasColumnType("numeric(18,6)");

        builder.Property(x => x.Volume)
            .IsRequired();

        builder.HasIndex(x => new { x.Symbol, x.Resolution, x.TimeStampUtc })
            .IsUnique();
    }
}