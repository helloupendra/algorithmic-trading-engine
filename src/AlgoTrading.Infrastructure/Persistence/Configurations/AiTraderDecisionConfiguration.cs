using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="AiTraderDecision"/>.</summary>
public class AiTraderDecisionConfiguration : IEntityTypeConfiguration<AiTraderDecision>
{
    public void Configure(EntityTypeBuilder<AiTraderDecision> builder)
    {
        builder.ToTable("ai_trader_decisions", table =>
            table.HasCheckConstraint("CK_ai_trader_decisions_Mode", IncidentConfiguration.SqlIn("Mode", AiTraderModes.All)));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Mode).IsRequired().HasMaxLength(16);
        builder.Property(x => x.BriefHash).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Brief).IsRequired();
        builder.Property(x => x.Model).IsRequired().HasMaxLength(128).HasDefaultValue(string.Empty);
        builder.Property(x => x.Action).IsRequired().HasMaxLength(32).HasDefaultValue(string.Empty);
        builder.Property(x => x.Underlying).IsRequired().HasMaxLength(32).HasDefaultValue(string.Empty);
        builder.Property(x => x.PlanJson).IsRequired();
        builder.Property(x => x.Reason).IsRequired().HasMaxLength(1000).HasDefaultValue(string.Empty);
        builder.Property(x => x.Rule).IsRequired().HasMaxLength(32).HasDefaultValue(string.Empty);
        builder.Property(x => x.Why).IsRequired().HasMaxLength(1000).HasDefaultValue(string.Empty);
        builder.Property(x => x.ResultJson).IsRequired();
        builder.Property(x => x.Error).IsRequired().HasMaxLength(1000).HasDefaultValue(string.Empty);

        // The day's decisions, newest first: the Today card, the digest, the review.
        builder.HasIndex(x => new { x.Day, x.ClockUtc }, "IX_ai_trader_decisions_Day_Clock");
        builder.HasIndex(x => x.ReplaySessionId, "IX_ai_trader_decisions_ReplaySession");
    }
}
