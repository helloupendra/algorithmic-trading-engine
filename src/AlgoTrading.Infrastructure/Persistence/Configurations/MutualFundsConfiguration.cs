using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="MutualFundScheme"/>.</summary>
public class MutualFundSchemeConfiguration : IEntityTypeConfiguration<MutualFundScheme>
{
    public void Configure(EntityTypeBuilder<MutualFundScheme> builder)
    {
        builder.ToTable("mf_schemes");
        builder.HasKey(x => x.SchemeCode);
        // AMFI assigns the code; it is never generated here.
        builder.Property(x => x.SchemeCode).ValueGeneratedNever();

        builder.Property(x => x.IsinGrowth).HasMaxLength(12);
        builder.Property(x => x.IsinReinvest).HasMaxLength(12);
        builder.Property(x => x.Amc).IsRequired().HasMaxLength(120);
        builder.Property(x => x.SchemeName).IsRequired().HasMaxLength(300);
        builder.Property(x => x.Plan).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Option).IsRequired().HasMaxLength(10);
        builder.Property(x => x.OptionLabel).HasMaxLength(120);
        builder.Property(x => x.Category).IsRequired().HasMaxLength(200);
        builder.Property(x => x.LastNav).HasColumnType("numeric");
        builder.Property(x => x.FirstSeenUtc).IsRequired();
        builder.Property(x => x.LastSeenUtc).IsRequired();

        // Not unique: AMFI gives the same growth ISIN to two codes in a few cases.
        builder.HasIndex(x => x.IsinGrowth);
        builder.HasIndex(x => x.Amc);
    }
}

/// <summary>EF configuration for <see cref="MutualFundNav"/>.</summary>
public class MutualFundNavConfiguration : IEntityTypeConfiguration<MutualFundNav>
{
    public void Configure(EntityTypeBuilder<MutualFundNav> builder)
    {
        builder.ToTable("mf_nav");

        // (SchemeCode, Date): one NAV per scheme per day, and it includes Date,
        // the hypertable's partitioning column, as every unique index must. It
        // is also the index every read uses: one scheme over a date range.
        builder.HasKey(x => new { x.SchemeCode, x.Date });

        // Exactly as AMFI wrote it: NAVs come with 2 to 8 decimals.
        builder.Property(x => x.Nav).HasColumnType("numeric");
        builder.Property(x => x.Source).IsRequired().HasMaxLength(16);
    }
}

/// <summary>EF configuration for <see cref="MutualFundBenchmarkValue"/>.</summary>
public class MutualFundBenchmarkValueConfiguration : IEntityTypeConfiguration<MutualFundBenchmarkValue>
{
    public void Configure(EntityTypeBuilder<MutualFundBenchmarkValue> builder)
    {
        builder.ToTable("mf_benchmark_tri");

        // One value per index per day; it includes Date, the hypertable's partitioning column. Every read is one
        // index over a date range.
        builder.HasKey(x => new { x.IndexName, x.Date });
        builder.Property(x => x.IndexName).HasMaxLength(60);
        builder.Property(x => x.Value).HasColumnType("numeric");
    }
}

/// <summary>EF configuration for <see cref="MutualFundTer"/>.</summary>
public class MutualFundTerConfiguration : IEntityTypeConfiguration<MutualFundTer>
{
    public void Configure(EntityTypeBuilder<MutualFundTer> builder)
    {
        builder.ToTable("mf_ter");
        builder.HasKey(x => new { x.NsdlSchemeCode, x.Date });
        builder.Property(x => x.NsdlSchemeCode).HasMaxLength(120);
        builder.Property(x => x.SchemeName).IsRequired().HasMaxLength(300);
        builder.Property(x => x.NameKey).IsRequired().HasMaxLength(300);
        builder.Property(x => x.SchemeType).IsRequired().HasMaxLength(40);
        builder.Property(x => x.Category).IsRequired().HasMaxLength(200);
        builder.Property(x => x.RegularTer).HasColumnType("numeric");
        builder.Property(x => x.DirectTer).HasColumnType("numeric");

        // The report looks a scheme's TER up by its name.
        builder.HasIndex(x => new { x.NameKey, x.Date });
    }
}

/// <summary>EF configuration for <see cref="MutualFundSchemeStats"/>.</summary>
public class MutualFundSchemeStatsConfiguration : IEntityTypeConfiguration<MutualFundSchemeStats>
{
    public void Configure(EntityTypeBuilder<MutualFundSchemeStats> builder)
    {
        builder.ToTable("mf_scheme_stats");
        builder.HasKey(x => x.SchemeCode);
        builder.Property(x => x.SchemeCode).ValueGeneratedNever();
        builder.Property(x => x.PeerKey).IsRequired().HasMaxLength(260);
        builder.Property(x => x.Plan).IsRequired().HasMaxLength(10);

        // A report reads its scheme's peers: same group, same plan.
        builder.HasIndex(x => new { x.PeerKey, x.Plan });
    }
}
