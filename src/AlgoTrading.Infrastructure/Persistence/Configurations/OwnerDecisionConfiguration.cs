using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="OwnerDecision"/>.</summary>
public class OwnerDecisionConfiguration : IEntityTypeConfiguration<OwnerDecision>
{
    public void Configure(EntityTypeBuilder<OwnerDecision> builder)
    {
        builder.ToTable("owner_decisions", table =>
            table.HasCheckConstraint("CK_owner_decisions_Status", IncidentConfiguration.SqlIn("Status", OwnerDecision.Statuses)));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).IsRequired().HasMaxLength(OwnerDecision.TitleMax);
        builder.Property(x => x.Decided).IsRequired().HasMaxLength(OwnerDecision.DecidedMax);
        builder.Property(x => x.By).IsRequired().HasMaxLength(100);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(16);
        builder.Property(x => x.RecordedBy).IsRequired().HasMaxLength(100).HasDefaultValue(string.Empty);

        // The same date and title is the same decision: recording it again replaces it.
        builder.HasIndex(x => new { x.Date, x.Title }, "UX_owner_decisions_Date_Title").IsUnique();
    }
}
