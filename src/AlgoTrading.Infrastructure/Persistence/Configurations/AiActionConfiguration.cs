using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="AiAction"/>.</summary>
public class AiActionConfiguration : IEntityTypeConfiguration<AiAction>
{
    public void Configure(EntityTypeBuilder<AiAction> builder)
    {
        builder.ToTable("ai_actions", table =>
        {
            table.HasCheckConstraint("CK_ai_actions_Status", IncidentConfiguration.SqlIn("Status", AiActionStatus.All));
            table.HasCheckConstraint("CK_ai_actions_Kind", IncidentConfiguration.SqlIn("Kind", AiActionKind.All));
            table.HasCheckConstraint("CK_ai_actions_Channel", IncidentConfiguration.SqlIn("Channel", AiActionChannel.All));
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserName).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Channel).IsRequired().HasMaxLength(16);
        builder.Property(x => x.ConversationId).IsRequired().HasMaxLength(64).HasDefaultValue(string.Empty);
        builder.Property(x => x.Kind).IsRequired().HasMaxLength(32);
        builder.Property(x => x.ParamsJson).IsRequired().HasColumnType("text").HasDefaultValue("{}");
        builder.Property(x => x.Summary).IsRequired().HasMaxLength(1000);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(16);
        builder.Property(x => x.ResultJson).HasColumnType("text");
        builder.Property(x => x.Error).IsRequired().HasMaxLength(1000).HasDefaultValue(string.Empty);

        // A PIN on Telegram confirms the chat's pending action; a proposal replaces the conversation's older one.
        builder.HasIndex(x => new { x.ConversationId, x.Status }, "IX_ai_actions_ConversationId_Status");
        builder.HasIndex(x => new { x.UserId, x.CreatedUtc }, "IX_ai_actions_UserId_CreatedUtc");
    }
}
