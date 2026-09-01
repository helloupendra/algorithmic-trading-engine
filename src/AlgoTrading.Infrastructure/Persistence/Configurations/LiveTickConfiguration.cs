using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System;
using System.Collections.Generic;
using System.Text;

namespace AlgoTrading.Infrastructure.Persistence.Configurations
{

    /// <summary>
    /// Entity Framework Core configuration for the <see cref="LiveTick"/> entity.
    /// Manages the schema for storing highly granular, real-time streaming market ticks.
    /// </summary>
    public class LiveTickConfiguration : IEntityTypeConfiguration<LiveTick>
    {
        public void Configure(EntityTypeBuilder<LiveTick> builder)
        {
            builder.ToTable("live_ticks");

            builder.HasKey(x => x.Id);

            builder.Property(x => x.Symbol)
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(x => x.DataType)
                .IsRequired()
                .HasMaxLength(50);

            builder.Property(x => x.LastTradedPrice)
                .HasColumnType("numeric(18,6)");

            builder.Property(x => x.BidPrice)
                .HasColumnType("numeric(18,6)");

            builder.Property(x => x.AskPrice)
                .HasColumnType("numeric(18,6)");

            builder.Property(x => x.Open)
                .HasColumnType("numeric(18,6)");

            builder.Property(x => x.High)
                .HasColumnType("numeric(18,6)");

            builder.Property(x => x.Low)
                .HasColumnType("numeric(18,6)");

            builder.Property(x => x.PrevClose)
                .HasColumnType("numeric(18,6)");

            builder.Property(x => x.RawPayload)
                .HasColumnType("text");

            builder.Property(x => x.ReceivedUtc)
                .IsRequired();

            builder.HasIndex(x => new { x.Symbol, x.ReceivedUtc });
        }
    }

}
