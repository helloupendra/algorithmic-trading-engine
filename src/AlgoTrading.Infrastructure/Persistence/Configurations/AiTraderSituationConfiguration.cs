using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="AiTraderSituation"/>.</summary>
public class AiTraderSituationConfiguration : IEntityTypeConfiguration<AiTraderSituation>
{
    public void Configure(EntityTypeBuilder<AiTraderSituation> builder)
    {
        builder.ToTable("ai_trader_situations");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Underlying).IsRequired().HasMaxLength(32);
        builder.Property(x => x.Price).HasColumnType("numeric(18,6)");
        builder.Property(x => x.EmaSide).IsRequired().HasMaxLength(8);

        // One row per index, day and slot; the base rates read one index's rows, oldest first.
        builder.HasIndex(x => new { x.Underlying, x.Day, x.Slot }, "IX_ai_trader_situations_Underlying_Day_Slot").IsUnique();
    }
}
