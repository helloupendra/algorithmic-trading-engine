using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="AiPhoneToken"/>.</summary>
public class AiPhoneTokenConfiguration : IEntityTypeConfiguration<AiPhoneToken>
{
    public void Configure(EntityTypeBuilder<AiPhoneToken> builder)
    {
        builder.ToTable("ai_phone_tokens");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserName).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Label).IsRequired().HasMaxLength(60);
        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);

        // Every ask looks its token up by hash; two tokens never share one.
        builder.HasIndex(x => x.TokenHash, "IX_ai_phone_tokens_TokenHash").IsUnique();
        builder.HasIndex(x => new { x.UserId, x.CreatedUtc }, "IX_ai_phone_tokens_UserId_CreatedUtc");
    }
}
