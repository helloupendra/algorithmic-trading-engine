using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations
{
    public class ExpiryRuleConfiguration : IEntityTypeConfiguration<ExpiryRule>
    {
        public void Configure(EntityTypeBuilder<ExpiryRule> builder)
        {
            builder.ToTable("expiry_rules");

            builder.HasKey(x => x.Id);

            builder.Property(x => x.Exchange)
                .IsRequired()
                .HasMaxLength(50);

            builder.Property(x => x.Underlying)
                .IsRequired()
                .HasMaxLength(50);

            builder.Property(x => x.CreatedUtc).IsRequired();
            builder.Property(x => x.UpdatedUtc).IsRequired();

            builder.HasIndex(x => new { x.Exchange, x.Underlying })
                .IsUnique();
        }
    }
}
