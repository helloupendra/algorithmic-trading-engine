using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="RegulatoryCircular"/>.</summary>
public class RegulatoryCircularConfiguration : IEntityTypeConfiguration<RegulatoryCircular>
{
    public void Configure(EntityTypeBuilder<RegulatoryCircular> builder)
    {
        builder.ToTable("regulatory_circulars");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Source).IsRequired().HasMaxLength(8);
        builder.Property(x => x.Key).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Number).IsRequired().HasMaxLength(200).HasDefaultValue(string.Empty);
        builder.Property(x => x.Title).IsRequired().HasColumnType("text");
        builder.Property(x => x.Department).IsRequired().HasMaxLength(200).HasDefaultValue(string.Empty);
        builder.Property(x => x.Url).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.DocumentUrl).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.Excerpt).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.Relevance).IsRequired().HasMaxLength(200).HasDefaultValue(string.Empty);
        builder.Property(x => x.FirstSeenUtc).IsRequired();

        // The duplicate check: a circular is stored once, on its first sighting.
        builder.HasIndex(x => x.Key).IsUnique();

        // The weekly triage reads what was recorded since a day.
        builder.HasIndex(x => x.FirstSeenUtc);
    }
}
