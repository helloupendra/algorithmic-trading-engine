using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="Incident"/>.</summary>
/// <remarks>
/// The table and column names are read and written by Sentinel's own SQL in
/// <c>sentinel/store.py</c>, not only by EF, so they are the default quoted
/// PascalCase on purpose and must not be renamed on one side alone
/// (<c>IncidentsTableContractTests</c> reads store.py and checks every name it uses).
/// </remarks>
public class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> builder)
    {
        builder.ToTable("incidents", table =>
        {
            // The writer is a script, so the spelling is checked where it
            // lands. A row with "Open" or "High" would otherwise be stored and
            // then match neither the live list nor the severity counts: an
            // incident that reads as nothing wrong. Refused at insert, it goes
            // to Sentinel's fallback file and to Telegram instead.
            table.HasCheckConstraint("CK_incidents_Status", SqlIn("Status", IncidentStatus.All));
            table.HasCheckConstraint("CK_incidents_Severity", SqlIn("Severity", IncidentSeverity.All));
        });

        builder.HasKey(x => x.Id);

        // store.py cuts Title and Location to 300 and Finding refuses a
        // fingerprint over 200. Nothing cuts the rule, so it is unbounded
        // rather than a length that one long rule name would fail at insert.
        // Agent names are a handful of short module names; 64 is headroom.
        builder.Property(x => x.Fingerprint).IsRequired().HasMaxLength(200);
        builder.Property(x => x.Agent).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Rule).IsRequired().HasColumnType("text");
        builder.Property(x => x.Severity).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Title).IsRequired().HasMaxLength(300);
        builder.Property(x => x.Summary).HasColumnType("text");
        builder.Property(x => x.Location).HasMaxLength(300);
        builder.Property(x => x.EvidenceJson).HasColumnType("text");
        builder.Property(x => x.Suggestion).HasColumnType("text");
        builder.Property(x => x.AcknowledgedBy).HasMaxLength(100);
        builder.Property(x => x.ResolvedBy).HasMaxLength(100);

        // Two writers change a row's status: a person in the console, and
        // Sentinel resolving it after enough clean checks. Without the token an
        // acknowledge that lands just after Sentinel's resolve would put the
        // row back to "acknowledged" and leave it live for good; with it, the
        // second writer's update matches nothing and the API answers 409.
        // Status only: Sentinel rewrites Occurrences, LastSeenUtc, Severity and
        // the evidence of every live row on every check, so a token covering
        // those (or the whole row, like xmin) would turn every acknowledge
        // during an active incident into a 409.
        builder.Property(x => x.Status).IsRequired().HasMaxLength(16).IsConcurrencyToken();

        // The console's question: what is live now, most recent first.
        builder.HasIndex(x => new { x.Status, x.LastSeenUtc })
            .IsDescending(false, true)
            .HasDatabaseName("IX_incidents_Status_LastSeenUtc");

        // Every episode of one problem, including the resolved ones.
        builder.HasIndex(x => x.Fingerprint, "IX_incidents_Fingerprint");

        // One live incident per problem. Sentinel's upsert looks for a live row
        // before inserting; if two of its checks race, the second insert fails
        // here instead of leaving two rows that each collect half the sightings.
        builder.HasIndex(x => x.Fingerprint, "UX_incidents_Fingerprint_live")
            .IsUnique()
            .HasFilter(SqlIn("Status", IncidentStatus.Live));
    }

    /// <summary><c>"Column" IN ('a', 'b')</c>, from the same constants the API compares against.</summary>
    internal static string SqlIn(string column, IEnumerable<string> values) =>
        $"\"{column}\" IN ({string.Join(", ", values.Select(v => $"'{v}'"))})";
}

/// <summary>EF configuration for <see cref="SentinelHeartbeat"/>.</summary>
/// <remarks>
/// One row, key 1, which Sentinel upserts at the end of every round of checks:
/// <c>INSERT INTO sentinel_heartbeat ("Id", "LastCheckUtc") VALUES (1, now)
/// ON CONFLICT ("Id") DO UPDATE SET "LastCheckUtc" = EXCLUDED."LastCheckUtc"</c>.
/// </remarks>
public class SentinelHeartbeatConfiguration : IEntityTypeConfiguration<SentinelHeartbeat>
{
    public void Configure(EntityTypeBuilder<SentinelHeartbeat> builder)
    {
        builder.ToTable("sentinel_heartbeat", table =>
            table.HasCheckConstraint("CK_sentinel_heartbeat_single_row", $"\"Id\" = {SentinelHeartbeat.SingletonId}"));

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.LastCheckUtc).IsRequired();
    }
}
