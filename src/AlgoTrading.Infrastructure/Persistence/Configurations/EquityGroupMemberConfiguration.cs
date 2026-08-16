// src/AlgoTrading.Infrastructure/Persistence/Configurations/EquityGroupMemberConfiguration.cs
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

public class EquityGroupMemberConfiguration : IEntityTypeConfiguration<EquityGroupMember>
{
    public void Configure(EntityTypeBuilder<EquityGroupMember> builder)
    {
        builder.ToTable("equity_group_members");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.Symbol)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.Weight)
            .HasColumnType("numeric(18,6)");

        builder.Property(x => x.CreatedUtc).IsRequired();
        builder.Property(x => x.UpdatedUtc).IsRequired();

        builder.HasOne(x => x.EquityGroup)
            .WithMany(x => x.Members)
            .HasForeignKey(x => x.EquityGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // Prevent duplicate same-symbol rows for same group and effective date range pattern
        builder.HasIndex(x => new { x.EquityGroupId, x.Symbol, x.EffectiveFrom, x.EffectiveTo })
            .IsUnique();

        builder.HasIndex(x => new { x.EquityGroupId, x.IsEnabled });
        builder.HasIndex(x => x.Symbol);
    }
}