
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>
/// Entity Framework Core configuration for the <see cref="LiveIngestorStatus"/> entity.
/// Defines table mapping for tracking the health and active subscriptions of background workers.
/// </summary>
public class LiveIngestorStatusConfiguration : IEntityTypeConfiguration<LiveIngestorStatus>
{
    public void Configure(EntityTypeBuilder<LiveIngestorStatus> builder)
    {
        builder.ToTable("live_ingestor_status");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.SourceName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.CurrentSubscribedSymbolsJson)
            .HasColumnType("text");

        builder.Property(x => x.LastError)
            .HasColumnType("text");

        builder.Property(x => x.LastHeartbeatUtc)
            .IsRequired();

        builder.Property(x => x.UpdatedUtc)
            .IsRequired();

        builder.HasIndex(x => x.SourceName)
            .IsUnique();
    }
}
