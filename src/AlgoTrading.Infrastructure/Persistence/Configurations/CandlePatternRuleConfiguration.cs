using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="CandlePatternRule"/>.</summary>
public class CandlePatternRuleConfiguration : IEntityTypeConfiguration<CandlePatternRule>
{
    public void Configure(EntityTypeBuilder<CandlePatternRule> builder)
    {
        builder.ToTable("candle_pattern_rules");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(120);
        builder.Property(x => x.SymbolsCsv).IsRequired().HasColumnType("text");
        builder.Property(x => x.GroupsCsv).IsRequired().HasMaxLength(500);
        builder.Property(x => x.TimeframesCsv).IsRequired().HasMaxLength(50);
        builder.Property(x => x.PatternsCsv).IsRequired().HasMaxLength(500);
        builder.Property(x => x.IsEnabled).IsRequired();
        builder.Property(x => x.Notify).IsRequired();
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);
        builder.Property(x => x.CreatedUtc).IsRequired();
        builder.Property(x => x.UpdatedUtc).IsRequired();
    }
}
