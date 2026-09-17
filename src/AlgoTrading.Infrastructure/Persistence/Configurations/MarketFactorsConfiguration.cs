using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="MarketParticipantOpenInterest"/>.</summary>
public class MarketParticipantOpenInterestConfiguration : IEntityTypeConfiguration<MarketParticipantOpenInterest>
{
    public void Configure(EntityTypeBuilder<MarketParticipantOpenInterest> builder)
    {
        builder.ToTable("market_participant_oi");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Date).IsRequired();
        builder.Property(x => x.ClientType).IsRequired().HasMaxLength(20);
        builder.Property(x => x.Source).IsRequired().HasMaxLength(300);
        builder.Property(x => x.FetchedUtc).IsRequired();

        // One row per participant group per day; a re-fetch replaces it.
        builder.HasIndex(x => new { x.Date, x.ClientType }).IsUnique();
    }
}

/// <summary>EF configuration for <see cref="MarketCashFlow"/>.</summary>
public class MarketCashFlowConfiguration : IEntityTypeConfiguration<MarketCashFlow>
{
    public void Configure(EntityTypeBuilder<MarketCashFlow> builder)
    {
        builder.ToTable("market_cash_flows");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Date).IsRequired();
        builder.Property(x => x.Category).IsRequired().HasMaxLength(20);
        builder.Property(x => x.BuyValueCrore).HasPrecision(18, 2);
        builder.Property(x => x.SellValueCrore).HasPrecision(18, 2);
        builder.Property(x => x.NetValueCrore).HasPrecision(18, 2);
        builder.Property(x => x.Source).IsRequired().HasMaxLength(300);
        builder.Property(x => x.FetchedUtc).IsRequired();

        builder.HasIndex(x => new { x.Date, x.Category }).IsUnique();
    }
}

/// <summary>EF configuration for <see cref="MarketFuturesDaily"/>.</summary>
public class MarketFuturesDailyConfiguration : IEntityTypeConfiguration<MarketFuturesDaily>
{
    public void Configure(EntityTypeBuilder<MarketFuturesDaily> builder)
    {
        builder.ToTable("market_futures_daily");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Date).IsRequired();
        builder.Property(x => x.Underlying).IsRequired().HasMaxLength(40);
        builder.Property(x => x.InstrumentKind).IsRequired().HasMaxLength(8);
        builder.Property(x => x.ExpiryDate).IsRequired();
        builder.Property(x => x.Close).HasPrecision(18, 2);
        builder.Property(x => x.PreviousClose).HasPrecision(18, 2);
        builder.Property(x => x.SettlementPrice).HasPrecision(18, 2);
        builder.Property(x => x.UnderlyingPrice).HasPrecision(18, 2);
        builder.Property(x => x.TurnoverValue).HasPrecision(22, 2);
        builder.Property(x => x.Source).IsRequired().HasMaxLength(300);
        builder.Property(x => x.FetchedUtc).IsRequired();

        builder.HasIndex(x => new { x.Date, x.Underlying, x.ExpiryDate }).IsUnique();
        builder.HasIndex(x => new { x.Underlying, x.Date });
    }
}

/// <summary>EF configuration for <see cref="MarketEvent"/>.</summary>
public class MarketEventConfiguration : IEntityTypeConfiguration<MarketEvent>
{
    public void Configure(EntityTypeBuilder<MarketEvent> builder)
    {
        builder.ToTable("market_events");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Date).IsRequired();
        builder.Property(x => x.Region).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Category).IsRequired().HasMaxLength(40);
        builder.Property(x => x.Title).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Notes).HasMaxLength(1000);
        builder.Property(x => x.Source).HasMaxLength(300);
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);
        builder.Property(x => x.CreatedUtc).IsRequired();
        builder.Property(x => x.UpdatedUtc).IsRequired();

        builder.HasIndex(x => x.Date);
        // The seeder matches shipped rows on these, so a re-seed never duplicates.
        builder.HasIndex(x => new { x.Date, x.Category, x.Title }).IsUnique();
    }
}
