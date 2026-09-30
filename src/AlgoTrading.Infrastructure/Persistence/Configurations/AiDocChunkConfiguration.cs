using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="AiDocChunk"/>.</summary>
public class AiDocChunkConfiguration : IEntityTypeConfiguration<AiDocChunk>
{
    public void Configure(EntityTypeBuilder<AiDocChunk> builder)
    {
        builder.ToTable("ai_doc_chunks");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Path).IsRequired().HasMaxLength(256);
        builder.Property(x => x.Heading).IsRequired().HasMaxLength(400);
        builder.Property(x => x.Text).IsRequired().HasColumnType("text");
        builder.Property(x => x.Hash).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Embedding).IsRequired().HasColumnType("real[]");
        builder.Property(x => x.Model).IsRequired().HasMaxLength(128);
        builder.HasIndex(x => new { x.Path, x.Ordinal }, "IX_ai_doc_chunks_Path_Ordinal");
    }
}
