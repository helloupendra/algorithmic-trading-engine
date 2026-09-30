using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="AiReport"/>.</summary>
public class AiReportConfiguration : IEntityTypeConfiguration<AiReport>
{
    public void Configure(EntityTypeBuilder<AiReport> builder)
    {
        builder.ToTable("ai_reports", table =>
        {
            table.HasCheckConstraint("CK_ai_reports_Status", IncidentConfiguration.SqlIn("Status", AiReportStatus.All));
            table.HasCheckConstraint("CK_ai_reports_SubjectType", IncidentConfiguration.SqlIn("SubjectType", AiReportSubject.All));
        });

        builder.HasKey(x => x.Id);

        builder.Property(x => x.AgentKey).IsRequired().HasMaxLength(64);
        builder.Property(x => x.SubjectType).IsRequired().HasMaxLength(16);
        builder.Property(x => x.SubjectId).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Model).IsRequired().HasMaxLength(128).HasDefaultValue(string.Empty);
        builder.Property(x => x.Title).IsRequired().HasMaxLength(300).HasDefaultValue(string.Empty);
        builder.Property(x => x.Body).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.DataJson).IsRequired().HasColumnType("text").HasDefaultValue("{}");
        builder.Property(x => x.Error).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);

        // One report per agent per subject: a scheduled agent that runs again finds its work done.
        builder.HasIndex(x => new { x.AgentKey, x.SubjectType, x.SubjectId }, "UX_ai_reports_Agent_Subject").IsUnique();

        // The Reports tab lists the newest, all or one agent's, often by day.
        builder.HasIndex(x => new { x.AgentKey, x.Id }, "IX_ai_reports_AgentKey_Id");
        builder.HasIndex(x => x.SessionDate, "IX_ai_reports_SessionDate");
    }
}
