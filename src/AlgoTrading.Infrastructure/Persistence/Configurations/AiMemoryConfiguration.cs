using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="AiMemory"/>.</summary>
public class AiMemoryConfiguration : IEntityTypeConfiguration<AiMemory>
{
    public void Configure(EntityTypeBuilder<AiMemory> builder)
    {
        builder.ToTable("ai_memories", table =>
        {
            table.HasCheckConstraint("CK_ai_memories_Kind", IncidentConfiguration.SqlIn("Kind", AiMemoryKind.All));
            table.HasCheckConstraint("CK_ai_memories_Status", IncidentConfiguration.SqlIn("Status", AiMemoryStatus.All));
            table.HasCheckConstraint("CK_ai_memories_Source", IncidentConfiguration.SqlIn("Source", AiMemorySource.All));
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.AgentKey).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Kind).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Text).IsRequired().HasMaxLength(600);
        builder.Property(x => x.Context).IsRequired().HasMaxLength(400).HasDefaultValue(string.Empty);
        builder.Property(x => x.Source).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Via).IsRequired().HasMaxLength(16).HasDefaultValue("console");
        builder.Property(x => x.CreatedBy).IsRequired().HasMaxLength(100).HasDefaultValue(string.Empty);
        builder.Property(x => x.DecidedBy).IsRequired().HasMaxLength(100).HasDefaultValue(string.Empty);
        builder.Property(x => x.Uses).HasDefaultValue(0);
        builder.Property(x => x.Ups).HasDefaultValue(0);
        builder.Property(x => x.Downs).HasDefaultValue(0);
        builder.Property(x => x.CheckPasses).HasDefaultValue(0);
        builder.Property(x => x.CheckFails).HasDefaultValue(0);
        builder.Property(x => x.Embedding).IsRequired().HasColumnType("real[]");
        builder.Property(x => x.EmbeddingModel).IsRequired().HasMaxLength(128).HasDefaultValue(string.Empty);

        // Every call reads one agent's active memories.
        builder.HasIndex(x => new { x.AgentKey, x.Status }, "IX_ai_memories_AgentKey_Status");
    }
}
