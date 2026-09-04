using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

public class RiskEventConfiguration : IEntityTypeConfiguration<RiskEvent>
{
    public void Configure(EntityTypeBuilder<RiskEvent> builder)
    {
        builder.ToTable("risk_events");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Kind)
            .IsRequired()
            .HasMaxLength(40);

        builder.Property(x => x.ActorName)
            .HasMaxLength(100);

        builder.Property(x => x.Reason)
            .HasMaxLength(500);

        builder.Property(x => x.DetailsJson)
            .HasColumnType("text");

        builder.Property(x => x.Symbol)
            .HasMaxLength(100);

        builder.HasIndex(x => x.OccurredUtc).IsDescending();
        builder.HasIndex(x => x.Kind);
    }
}
