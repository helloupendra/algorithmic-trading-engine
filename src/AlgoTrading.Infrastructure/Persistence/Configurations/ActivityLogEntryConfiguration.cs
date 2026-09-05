using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

public class ActivityLogEntryConfiguration : IEntityTypeConfiguration<ActivityLogEntry>
{
    public void Configure(EntityTypeBuilder<ActivityLogEntry> builder)
    {
        builder.ToTable("activity_log");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.UserName).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Role).HasMaxLength(32);
        builder.Property(x => x.Module).IsRequired().HasMaxLength(40);
        builder.Property(x => x.Action).IsRequired().HasMaxLength(60);
        builder.Property(x => x.Method).IsRequired().HasMaxLength(10);
        builder.Property(x => x.Path).IsRequired().HasMaxLength(300);
        builder.Property(x => x.TargetType).HasMaxLength(40);
        builder.Property(x => x.TargetId).HasMaxLength(60);
        builder.Property(x => x.Summary).HasMaxLength(500);
        builder.Property(x => x.IpAddress).HasMaxLength(64);

        // The three questions this table is asked: what happened recently, what did
        // this person do, and what happened in this module.
        builder.HasIndex(x => x.OccurredUtc);
        builder.HasIndex(x => new { x.UserId, x.OccurredUtc });
        builder.HasIndex(x => new { x.Module, x.OccurredUtc });
    }
}
