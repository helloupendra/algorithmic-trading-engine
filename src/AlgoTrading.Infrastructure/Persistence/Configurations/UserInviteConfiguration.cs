using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

public class UserInviteConfiguration : IEntityTypeConfiguration<UserInvite>
{
    public void Configure(EntityTypeBuilder<UserInvite> builder)
    {
        builder.ToTable("user_invites");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.TokenHash).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Email).IsRequired().HasMaxLength(256);
        builder.Property(x => x.SuggestedUserName).HasMaxLength(100);
        builder.Property(x => x.ModuleKeysCsv).HasMaxLength(300);
        builder.Property(x => x.CreatedBy).HasMaxLength(100);

        // The hash is how an invite is looked up, so it must be unique and indexed.
        builder.HasIndex(x => x.TokenHash).IsUnique();
        builder.HasIndex(x => x.Email);
    }
}
