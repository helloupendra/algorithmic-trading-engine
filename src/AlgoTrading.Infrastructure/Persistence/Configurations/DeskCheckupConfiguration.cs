using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="DeskCheckup"/>.</summary>
/// <remarks>
/// Like the incidents table, the names are read and written by Sentinel's own
/// SQL (<c>sentinel/checkup/store.py</c>), so they are the default quoted
/// PascalCase on purpose and must not be renamed on one side alone
/// (<c>DeskCheckupsTableContractTests</c> checks every name store.py uses).
/// </remarks>
public class DeskCheckupConfiguration : IEntityTypeConfiguration<DeskCheckup>
{
    public void Configure(EntityTypeBuilder<DeskCheckup> builder)
    {
        builder.ToTable("desk_checkups", table =>
            // The API finds the latest and the pending checkup by this column,
            // so a row with "Done" or "complete" would be stored and then never
            // shown: a checkup that ran and reads as one that did not. Refused
            // at insert, the failure is Sentinel's to report instead.
            table.HasCheckConstraint("CK_desk_checkups_Status", IncidentConfiguration.SqlIn("Status", DeskCheckupStatus.All)));

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Slot).IsRequired().HasMaxLength(32);

        // The API's only update is failing a request nobody picked up. Without
        // the token, one that Sentinel starts in the same instant would be
        // overwritten back to "failed" while it runs; with it, the API's update
        // matches nothing and it waits for the running checkup instead.
        builder.Property(x => x.Status).IsRequired().HasMaxLength(16).IsConcurrencyToken();

        // Every text column has a database default, so Sentinel's INSERT can
        // name only the columns it has at that moment (a scheduled checkup has
        // no requester, a running one no verdict yet).
        builder.Property(x => x.RequestedBy).IsRequired().HasMaxLength(100).HasDefaultValue(string.Empty);
        builder.Property(x => x.Verdict).IsRequired().HasMaxLength(16).HasDefaultValue(string.Empty);
        builder.Property(x => x.Headline).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.ItemsJson).IsRequired().HasColumnType("text").HasDefaultValue("[]");
        builder.Property(x => x.Error).IsRequired().HasColumnType("text").HasDefaultValue(string.Empty);
        builder.Property(x => x.Host).IsRequired().HasMaxLength(100).HasDefaultValue(string.Empty);

        // Sentinel looks for requested rows to pick up; the API for the pending
        // and the latest finished one.
        builder.HasIndex(x => x.Status, "IX_desk_checkups_Status");

        // "When was the desk last checked": the newest completion.
        builder.HasIndex(x => x.CompletedUtc, "IX_desk_checkups_CompletedUtc");
    }
}
