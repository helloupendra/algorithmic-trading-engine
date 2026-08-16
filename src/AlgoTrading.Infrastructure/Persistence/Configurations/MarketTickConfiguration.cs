// src/AlgoTrading.Infrastructure/Persistence/Configurations/MarketTickConfiguration.cs
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

public class MarketTickConfiguration : IEntityTypeConfiguration<MarketTick>
{
    public void Configure(EntityTypeBuilder<MarketTick> builder)
    {
        builder.ToTable("market_ticks");

        builder.HasKey(x => new { x.Id, x.ReceivedUtc });
        builder.Property(x => x.Id).UseIdentityColumn();

        builder.Property(x => x.Symbol)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.DataType)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.LastTradedPrice).HasColumnType("numeric(18,6)");
        builder.Property(x => x.BidPrice).HasColumnType("numeric(18,6)");
        builder.Property(x => x.AskPrice).HasColumnType("numeric(18,6)");

        builder.Property(x => x.BidSize).HasColumnType("numeric(18,6)");
        builder.Property(x => x.AskSize).HasColumnType("numeric(18,6)");

        builder.Property(x => x.Open).HasColumnType("numeric(18,6)");
        builder.Property(x => x.High).HasColumnType("numeric(18,6)");
        builder.Property(x => x.Low).HasColumnType("numeric(18,6)");
        builder.Property(x => x.PrevClose).HasColumnType("numeric(18,6)");
        builder.Property(x => x.Volume).HasColumnType("numeric(18,6)");

        builder.Property(x => x.RawPayload).HasColumnType("text");
        builder.Property(x => x.ReceivedUtc).IsRequired();

        builder.HasIndex(x => new { x.Symbol, x.ExchangeTimestampUtc });
        builder.HasIndex(x => x.ExchangeTimestampUtc);
        builder.HasIndex(x => x.ReceivedUtc);
    }
}