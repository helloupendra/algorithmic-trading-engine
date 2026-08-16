using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations
{
    /// <summary>
    /// Entity Framework Core configuration for the <see cref="Instrument"/> entity.
    /// Manages the schema for storing tradable master data.
    /// </summary>
    public class InstrumentConfiguration : IEntityTypeConfiguration<Instrument>
    {
        public void Configure(EntityTypeBuilder<Instrument> builder)
        {
            builder.ToTable("instruments");

            builder.HasKey(x => x.Id);

            builder.Property(x => x.Symbol).IsRequired().HasMaxLength(100);
            builder.Property(x => x.Exchange).IsRequired().HasMaxLength(50);
            builder.Property(x => x.Segment).HasMaxLength(50);
            builder.Property(x => x.Description).HasMaxLength(300);
            builder.Property(x => x.InstrumentType).HasMaxLength(50);
            builder.Property(x => x.Isin).HasMaxLength(50);

            builder.Property(x => x.TickSize).HasColumnType("numeric(18,6)");
            builder.Property(x => x.CreatedUtc).IsRequired();
            builder.Property(x => x.UpdatedUtc).IsRequired();
            builder.Property(x => x.IsEnabled).IsRequired();

            builder.HasIndex(x => x.Symbol).IsUnique();


            builder.Property(x => x.Underlying).HasMaxLength(100);
            builder.Property(x => x.StrikePrice).HasColumnType("numeric(18,2)");
            builder.Property(x => x.OptionType).HasMaxLength(10);

            builder.Property(x => x.CreatedUtc).IsRequired();
            builder.Property(x => x.UpdatedUtc).IsRequired();
            builder.Property(x => x.IsEnabled).IsRequired();

            builder.HasIndex(x => x.Symbol).IsUnique();
            builder.HasIndex(x => x.Underlying);
            builder.HasIndex(x => x.ExpiryDate);

        }
    }
}
