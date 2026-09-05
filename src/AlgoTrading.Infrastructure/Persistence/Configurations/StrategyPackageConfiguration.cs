using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

public class StrategyPackageConfiguration : IEntityTypeConfiguration<StrategyPackage>
{
    public void Configure(EntityTypeBuilder<StrategyPackage> builder)
    {
        builder.ToTable("strategy_packages");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Key).IsRequired().HasMaxLength(32);
        builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.Property(x => x.AllowedUnderlyingsCsv).HasMaxLength(300);
        builder.Property(x => x.CreatedBy).HasMaxLength(100);

        builder.HasIndex(x => x.Key).IsUnique();
    }
}

public class StrategyPackageItemConfiguration : IEntityTypeConfiguration<StrategyPackageItem>
{
    public void Configure(EntityTypeBuilder<StrategyPackageItem> builder)
    {
        builder.ToTable("strategy_package_items");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.StrategyName).IsRequired().HasMaxLength(120);

        builder.HasOne(x => x.Package)
            .WithMany(x => x.Items)
            .HasForeignKey(x => x.StrategyPackageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.StrategyPackageId, x.StrategyName }).IsUnique();
    }
}

public class UserStrategyGrantConfiguration : IEntityTypeConfiguration<UserStrategyGrant>
{
    public void Configure(EntityTypeBuilder<UserStrategyGrant> builder)
    {
        builder.ToTable("user_strategy_grants");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.StrategyName).IsRequired().HasMaxLength(120);
        builder.Property(x => x.GrantedBy).HasMaxLength(100);

        builder.HasOne(x => x.User)
            .WithMany(x => x.StrategyGrants)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.UserId, x.StrategyName }).IsUnique();
    }
}
