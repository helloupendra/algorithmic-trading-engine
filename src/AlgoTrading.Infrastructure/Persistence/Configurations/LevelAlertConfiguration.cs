using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="LevelAlertRule"/>.</summary>
public class LevelAlertRuleConfiguration : IEntityTypeConfiguration<LevelAlertRule>
{
    public void Configure(EntityTypeBuilder<LevelAlertRule> builder)
    {
        builder.ToTable("level_alert_rules", table =>
        {
            table.HasCheckConstraint("CK_level_alert_rules_Kind", IncidentConfiguration.SqlIn("Kind", LevelAlertKinds.All));
            table.HasCheckConstraint("CK_level_alert_rules_Condition", IncidentConfiguration.SqlIn("Condition", LevelAlertConditions.All));
            table.HasCheckConstraint("CK_level_alert_rules_Status", IncidentConfiguration.SqlIn("Status", LevelAlertStatus.Stored));
        });

        builder.HasKey(x => x.Id);

        // A user's rules go with their account, like the rest of what is theirs.
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.Kind).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Target).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Condition).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Level).HasColumnType("numeric(18,6)");
        builder.Property(x => x.RearmBy).HasColumnType("numeric(18,6)");
        builder.Property(x => x.Note).HasMaxLength(200);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(16);

        // The service reads the active rules every few seconds; the page lists one user's.
        builder.HasIndex(x => x.Status, "IX_level_alert_rules_Status");
        builder.HasIndex(x => x.UserId, "IX_level_alert_rules_UserId");
    }
}

/// <summary>EF configuration for <see cref="LevelAlertFire"/>.</summary>
public class LevelAlertFireConfiguration : IEntityTypeConfiguration<LevelAlertFire>
{
    public void Configure(EntityTypeBuilder<LevelAlertFire> builder)
    {
        builder.ToTable("level_alert_fires");

        builder.HasKey(x => x.Id);

        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.Kind).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Target).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Condition).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Level).HasColumnType("numeric(18,6)");
        builder.Property(x => x.Value).HasColumnType("numeric(18,6)");
        builder.Property(x => x.PreviousValue).HasColumnType("numeric(18,6)");
        builder.Property(x => x.Title).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Message).IsRequired().HasMaxLength(1000);
        builder.Property(x => x.TelegramNote).HasMaxLength(200);

        // The history, newest first, per owner; and the top bar's unseen count.
        builder.HasIndex(x => new { x.UserId, x.FiredUtc }, "IX_level_alert_fires_UserId_FiredUtc");
        builder.HasIndex(x => x.FiredUtc, "IX_level_alert_fires_FiredUtc");
        builder.HasIndex(x => x.RuleId, "IX_level_alert_fires_RuleId");
    }
}
