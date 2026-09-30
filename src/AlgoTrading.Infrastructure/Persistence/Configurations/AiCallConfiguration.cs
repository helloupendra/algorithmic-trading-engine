using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="AiCall"/>.</summary>
public class AiCallConfiguration : IEntityTypeConfiguration<AiCall>
{
    public void Configure(EntityTypeBuilder<AiCall> builder)
    {
        builder.ToTable("ai_calls", table =>
            table.HasCheckConstraint("CK_ai_calls_Outcome", IncidentConfiguration.SqlIn("Outcome", AiCallOutcome.All)));

        builder.HasKey(x => x.Id);

        builder.Property(x => x.AgentKey).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Tier).IsRequired().HasMaxLength(16).HasDefaultValue(string.Empty);
        builder.Property(x => x.Source).IsRequired().HasMaxLength(16).HasDefaultValue(string.Empty);
        builder.Property(x => x.RequestedBy).IsRequired().HasMaxLength(100).HasDefaultValue(string.Empty);
        builder.Property(x => x.ConversationId).IsRequired().HasMaxLength(64).HasDefaultValue(string.Empty);
        builder.Property(x => x.ChainJson).IsRequired().HasColumnType("text").HasDefaultValue("[]");
        builder.Property(x => x.Model).IsRequired().HasMaxLength(128).HasDefaultValue(string.Empty);
        builder.Property(x => x.Outcome).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Error).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.AttemptsJson).IsRequired().HasColumnType("text").HasDefaultValue("[]");
        builder.Property(x => x.ToolsJson).IsRequired().HasColumnType("text").HasDefaultValue("[]");
        builder.Property(x => x.Rounds).HasDefaultValue(0);
        builder.Property(x => x.ToolCalls).HasDefaultValue(0);
        builder.Property(x => x.SystemPrompt).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.MessagesJson).IsRequired().HasColumnType("text").HasDefaultValue("[]");
        builder.Property(x => x.Summary).IsRequired().HasMaxLength(200).HasDefaultValue(string.Empty);
        builder.Property(x => x.Answer).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.Reasoning).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.FinishReason).IsRequired().HasMaxLength(32).HasDefaultValue(string.Empty);
        builder.Property(x => x.MemoryIdsJson).IsRequired().HasColumnType("text").HasDefaultValue("[]");
        builder.Property(x => x.FeedbackNote).IsRequired().HasMaxLength(600).HasDefaultValue(string.Empty);
        builder.Property(x => x.FeedbackBy).IsRequired().HasMaxLength(100).HasDefaultValue(string.Empty);

        // The console reads the newest calls, all or one agent's.
        builder.HasIndex(x => x.CreatedUtc, "IX_ai_calls_CreatedUtc");
        builder.HasIndex(x => new { x.AgentKey, x.Id }, "IX_ai_calls_AgentKey_Id");
    }
}
