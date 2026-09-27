using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>
/// The scoring columns <see cref="NewsItem"/> and <see cref="CorporateAnnouncement"/>
/// share, shaped once so the two tables cannot drift apart.
/// </summary>
/// <remarks>
/// The Python scorer (<c>analysis/news.py</c>) writes these with its own SQL,
/// so the names are the default quoted PascalCase on purpose and must not be
/// renamed on one side alone (<c>NewsTablesContractTests</c> checks every name
/// the script uses). Named by string because the properties are declared on
/// <see cref="IScoredText"/>, which a lambda on the entity type would not reach.
/// </remarks>
internal static class ScoredTextColumns
{
    public static void Configure<T>(EntityTypeBuilder<T> builder) where T : class, IScoredText
    {
        builder.Property<decimal?>(nameof(IScoredText.Sentiment)).HasPrecision(4, 3);
        builder.Property<short?>(nameof(IScoredText.Importance));
        builder.Property<string>(nameof(IScoredText.Symbols)).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property<string>(nameof(IScoredText.Topics)).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property<DateTime?>(nameof(IScoredText.ScoredUtc));
        builder.Property<string>(nameof(IScoredText.ScoreModel)).IsRequired().HasMaxLength(80).HasDefaultValue(string.Empty);

        // The scorer's work queue is "ScoredUtc IS NULL".
        builder.HasIndex(nameof(IScoredText.ScoredUtc));
    }
}

/// <summary>EF configuration for <see cref="NewsItem"/>.</summary>
public class NewsItemConfiguration : IEntityTypeConfiguration<NewsItem>
{
    public void Configure(EntityTypeBuilder<NewsItem> builder)
    {
        builder.ToTable("news_items");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Source).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Category).IsRequired().HasMaxLength(40);
        builder.Property(x => x.Title).IsRequired().HasColumnType("text");
        builder.Property(x => x.Summary).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.Link).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.LinkHash).IsRequired().HasMaxLength(64);
        builder.Property(x => x.FirstSeenUtc).IsRequired();

        ScoredTextColumns.Configure(builder);

        // The duplicate check: a headline is stored once, on its first sighting.
        builder.HasIndex(x => x.LinkHash).IsUnique();

        // "What was known before 08:50", overall and per category.
        builder.HasIndex(x => x.FirstSeenUtc);
        builder.HasIndex(x => new { x.Category, x.FirstSeenUtc });
    }
}

/// <summary>EF configuration for <see cref="CorporateAnnouncement"/>.</summary>
public class CorporateAnnouncementConfiguration : IEntityTypeConfiguration<CorporateAnnouncement>
{
    public void Configure(EntityTypeBuilder<CorporateAnnouncement> builder)
    {
        builder.ToTable("corporate_announcements");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Exchange).IsRequired().HasMaxLength(8);
        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(40);
        builder.Property(x => x.Company).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.Subject).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.Details).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.AttachmentUrl).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.FirstSeenUtc).IsRequired();
        builder.Property(x => x.UniqueKey).IsRequired().HasMaxLength(64);

        ScoredTextColumns.Configure(builder);

        builder.HasIndex(x => x.UniqueKey).IsUnique();
        builder.HasIndex(x => new { x.Symbol, x.AnnouncedUtc });
        builder.HasIndex(x => x.FirstSeenUtc);
    }
}

/// <summary>EF configuration for <see cref="CorporateCalendarEvent"/>.</summary>
public class CorporateCalendarEventConfiguration : IEntityTypeConfiguration<CorporateCalendarEvent>
{
    public void Configure(EntityTypeBuilder<CorporateCalendarEvent> builder)
    {
        builder.ToTable("corporate_calendar");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Exchange).IsRequired().HasMaxLength(8);
        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(40);
        builder.Property(x => x.Company).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.Purpose).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.EventDate).IsRequired();
        builder.Property(x => x.FirstSeenUtc).IsRequired();
        builder.Property(x => x.UniqueKey).IsRequired().HasMaxLength(64);

        builder.HasIndex(x => x.UniqueKey).IsUnique();
        builder.HasIndex(x => new { x.EventDate, x.Symbol });
    }
}

/// <summary>EF configuration for <see cref="MarketGlobalDaily"/>.</summary>
public class MarketGlobalDailyConfiguration : IEntityTypeConfiguration<MarketGlobalDaily>
{
    public void Configure(EntityTypeBuilder<MarketGlobalDaily> builder)
    {
        builder.ToTable("market_global_daily");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(40);
        builder.Property(x => x.Date).IsRequired();
        builder.Property(x => x.Open).HasPrecision(18, 6);
        builder.Property(x => x.High).HasPrecision(18, 6);
        builder.Property(x => x.Low).HasPrecision(18, 6);
        builder.Property(x => x.Close).HasPrecision(18, 6);
        builder.Property(x => x.Volume).HasPrecision(20, 2);
        builder.Property(x => x.Source).IsRequired().HasMaxLength(40);
        builder.Property(x => x.FetchedUtc).IsRequired();

        // One bar per symbol per date; a re-fetch updates it in place.
        builder.HasIndex(x => new { x.Symbol, x.Date }).IsUnique();
    }
}

/// <summary>EF configuration for <see cref="MarketQuoteSnapshot"/>.</summary>
public class MarketQuoteSnapshotConfiguration : IEntityTypeConfiguration<MarketQuoteSnapshot>
{
    public void Configure(EntityTypeBuilder<MarketQuoteSnapshot> builder)
    {
        builder.ToTable("market_quote_snapshots");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Key).IsRequired().HasMaxLength(40);
        builder.Property(x => x.Price).HasPrecision(18, 6);
        builder.Property(x => x.PreviousClose).HasPrecision(18, 6);
        builder.Property(x => x.ChangePct).HasPrecision(9, 4);
        builder.Property(x => x.FetchedUtc).IsRequired();
        builder.Property(x => x.Source).IsRequired().HasMaxLength(40);

        // "The latest GIFTNIFTY before 08:50".
        builder.HasIndex(x => new { x.Key, x.FetchedUtc });
    }
}

/// <summary>EF configuration for <see cref="MarketBreadthDaily"/>.</summary>
public class MarketBreadthDailyConfiguration : IEntityTypeConfiguration<MarketBreadthDaily>
{
    public void Configure(EntityTypeBuilder<MarketBreadthDaily> builder)
    {
        builder.ToTable("market_breadth_daily");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Exchange).IsRequired().HasMaxLength(8);
        builder.Property(x => x.Date).IsRequired();
        builder.Property(x => x.TurnoverCr).HasPrecision(18, 2);
        builder.Property(x => x.Source).IsRequired().HasMaxLength(40);

        builder.HasIndex(x => new { x.Exchange, x.Date }).IsUnique();
    }
}
