using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="AiTraderShadowPosition"/>.</summary>
public class AiTraderShadowPositionConfiguration : IEntityTypeConfiguration<AiTraderShadowPosition>
{
    public void Configure(EntityTypeBuilder<AiTraderShadowPosition> builder)
    {
        builder.ToTable("ai_trader_shadow_positions", table =>
        {
            table.HasCheckConstraint("CK_ai_trader_shadow_positions_Mode",
                IncidentConfiguration.SqlIn("Mode", [AiTraderModes.Shadow, AiTraderModes.Replay]));
            table.HasCheckConstraint("CK_ai_trader_shadow_positions_OptionType", IncidentConfiguration.SqlIn("OptionType", ["CE", "PE"]));
        });

        builder.HasKey(x => x.Id);
        builder.Ignore(x => x.Units);
        builder.Property(x => x.Mode).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Underlying).IsRequired().HasMaxLength(32);
        builder.Property(x => x.OptionType).IsRequired().HasMaxLength(2);
        builder.Property(x => x.Strike).HasColumnType("numeric(18,6)");
        builder.Property(x => x.EntryPrice).HasColumnType("numeric(18,6)");
        builder.Property(x => x.StopLoss).HasColumnType("numeric(18,6)");
        builder.Property(x => x.Target).HasColumnType("numeric(18,6)");
        builder.Property(x => x.MarkPrice).HasColumnType("numeric(18,6)");
        builder.Property(x => x.ExitPrice).HasColumnType("numeric(18,6)");
        builder.Property(x => x.ExitReason).IsRequired().HasMaxLength(16).HasDefaultValue(string.Empty);
        builder.Property(x => x.Charges).HasColumnType("numeric(18,6)");
        builder.Property(x => x.NetPnl).HasColumnType("numeric(18,6)");

        builder.HasIndex(x => new { x.Day, x.ReplaySessionId }, "IX_ai_trader_shadow_positions_Day_Replay");
        builder.HasIndex(x => x.DecisionId, "IX_ai_trader_shadow_positions_Decision");
        // The minute check reads only the open ones.
        builder.HasIndex(x => x.ExitUtc, "IX_ai_trader_shadow_positions_Open").HasFilter("\"ExitUtc\" IS NULL");
    }
}
