using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="UserModuleGrant"/>.</summary>
public class UserModuleGrantConfiguration : IEntityTypeConfiguration<UserModuleGrant>
{
    public void Configure(EntityTypeBuilder<UserModuleGrant> builder)
    {
        builder.ToTable("user_module_grants");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.ModuleKey)
            .IsRequired()
            .HasMaxLength(60);

        builder.Property(x => x.GrantedBy).HasMaxLength(100);

        // A grant cannot outlive the account it belongs to.
        builder.HasOne(x => x.User)
            .WithMany(x => x.ModuleGrants)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.UserId, x.ModuleKey }).IsUnique();
    }
}
