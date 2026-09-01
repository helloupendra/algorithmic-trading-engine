using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>
/// Entity Framework Core configuration for the <see cref="LiveWatchlistItem"/> entity.
/// Defines the schema for symbols actively subscribed to real-time data streams.
/// </summary>
public class LiveWatchlistItemConfiguration : IEntityTypeConfiguration<LiveWatchlistItem>
{
    public void Configure(EntityTypeBuilder<LiveWatchlistItem> builder)
    {
        builder.ToTable("live_watchlist");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Symbol)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.DataType)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.IsActive)
            .IsRequired();

        builder.Property(x => x.CreatedUtc)
            .IsRequired();

        builder.Property(x => x.UpdatedUtc)
            .IsRequired();

        builder.HasIndex(x => x.Symbol)
            .IsUnique();
    }
}