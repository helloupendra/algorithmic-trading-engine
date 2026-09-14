using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="MarketHoliday"/>.</summary>
public class MarketHolidayConfiguration : IEntityTypeConfiguration<MarketHoliday>
{
    public void Configure(EntityTypeBuilder<MarketHoliday> builder)
    {
        builder.ToTable("market_holidays");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Exchange).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Date).IsRequired();
        builder.Property(x => x.Name).IsRequired().HasMaxLength(120);
        builder.Property(x => x.Closure).IsRequired();
        builder.Property(x => x.Source).HasMaxLength(200);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);
        builder.Property(x => x.CreatedUtc).IsRequired();
        builder.Property(x => x.UpdatedUtc).IsRequired();

        // One answer per exchange and day.
        builder.HasIndex(x => new { x.Exchange, x.Date }).IsUnique();
    }
}

/// <summary>EF configuration for <see cref="MarketSpecialSession"/>.</summary>
public class MarketSpecialSessionConfiguration : IEntityTypeConfiguration<MarketSpecialSession>
{
    public void Configure(EntityTypeBuilder<MarketSpecialSession> builder)
    {
        builder.ToTable("market_special_sessions");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Exchange).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Date).IsRequired();
        builder.Property(x => x.Name).IsRequired().HasMaxLength(120);
        builder.Property(x => x.OpenIst).IsRequired();
        builder.Property(x => x.CloseIst).IsRequired();
        builder.Property(x => x.Source).HasMaxLength(200);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);
        builder.Property(x => x.CreatedUtc).IsRequired();
        builder.Property(x => x.UpdatedUtc).IsRequired();

        builder.HasIndex(x => new { x.Exchange, x.Date }).IsUnique();
    }
}
