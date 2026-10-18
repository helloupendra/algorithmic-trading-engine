using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="ComplianceFollowUp"/>.</summary>
public class ComplianceFollowUpConfiguration : IEntityTypeConfiguration<ComplianceFollowUp>
{
    public void Configure(EntityTypeBuilder<ComplianceFollowUp> builder)
    {
        builder.ToTable("compliance_follow_ups");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.CreatedUtc).IsRequired();
        builder.Property(x => x.AlertText).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.HandledBy).IsRequired().HasMaxLength(100).HasDefaultValue(string.Empty);
        builder.Property(x => x.HandledNote).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);

        // One alert per circular, ever: the second writer's insert fails here.
        builder.HasIndex(x => x.CircularId).IsUnique();

        builder.HasOne<RegulatoryCircular>()
            .WithMany()
            .HasForeignKey(x => x.CircularId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
