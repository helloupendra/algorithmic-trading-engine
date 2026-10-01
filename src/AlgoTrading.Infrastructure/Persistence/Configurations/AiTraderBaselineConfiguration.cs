using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="AiTraderBaseline"/>.</summary>
public class AiTraderBaselineConfiguration : IEntityTypeConfiguration<AiTraderBaseline>
{
    public void Configure(EntityTypeBuilder<AiTraderBaseline> builder)
    {
        builder.ToTable("ai_trader_baselines");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Rule).IsRequired().HasMaxLength(48);
        builder.Property(x => x.Underlying).IsRequired().HasMaxLength(32);
        builder.Property(x => x.OptionType).IsRequired().HasMaxLength(2).HasDefaultValue(string.Empty);
        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(64).HasDefaultValue(string.Empty);
        builder.Property(x => x.EntryPrice).HasColumnType("numeric(18,6)");
        builder.Property(x => x.StopLoss).HasColumnType("numeric(18,6)");
        builder.Property(x => x.Target).HasColumnType("numeric(18,6)");
        builder.Property(x => x.ExitPrice).HasColumnType("numeric(18,6)");
        builder.Property(x => x.ExitReason).IsRequired().HasMaxLength(16).HasDefaultValue(string.Empty);
        builder.Property(x => x.Charges).HasColumnType("numeric(18,6)");
        builder.Property(x => x.NetPnl).HasColumnType("numeric(18,6)");
        builder.Property(x => x.Note).IsRequired().HasMaxLength(500).HasDefaultValue(string.Empty);

        // A day is scored once per rule.
        builder.HasIndex(x => new { x.Day, x.Rule }, "IX_ai_trader_baselines_Day_Rule").IsUnique();
    }
}
