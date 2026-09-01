using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace AlgoTrading.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
/// <summary>
/// Entity Framework Core configuration for the <see cref="LiveQuoteLatest"/> entity. 
/// Manages the schema for caching the most recent market quote for a symbol.
/// </summary>
public class LiveQuoteLatestConfiguration : IEntityTypeConfiguration<LiveQuoteLatest>
{
    public void Configure(EntityTypeBuilder<LiveQuoteLatest> builder)
{
    builder.ToTable("live_quotes_latest");

    builder.HasKey(x => x.Id);

    builder.Property(x => x.Symbol)
        .IsRequired()
        .HasMaxLength(100);

    builder.Property(x => x.DataType)
        .IsRequired()
        .HasMaxLength(50);

    builder.Property(x => x.LastTradedPrice)
        .HasColumnType("numeric(18,6)");

    builder.Property(x => x.Open)
        .HasColumnType("numeric(18,6)");

    builder.Property(x => x.High)
        .HasColumnType("numeric(18,6)");

    builder.Property(x => x.Low)
        .HasColumnType("numeric(18,6)");

    builder.Property(x => x.Close)
        .HasColumnType("numeric(18,6)");

    builder.Property(x => x.RawPayload)
        .HasColumnType("text");

    builder.Property(x => x.UpdatedUtc)
        .IsRequired();

    builder.HasIndex(x => x.Symbol)
        .IsUnique();
}
}


