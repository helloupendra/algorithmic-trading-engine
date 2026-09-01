using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations
{
    public class StrategyDefinitionConfiguration : IEntityTypeConfiguration<StrategyDefinition>
    {
        public void Configure(EntityTypeBuilder<StrategyDefinition> builder)
        {
            builder.ToTable("strategies");

            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).UseIdentityColumn();

            builder.Property(x => x.Name).IsRequired().HasMaxLength(100);
            builder.Property(x => x.Description).HasMaxLength(500);
            builder.Property(x => x.DefaultParametersJson).HasColumnType("jsonb");
            builder.Property(x => x.CreatedUtc).IsRequired();

            builder.HasIndex(x => x.Name).IsUnique();
        }
    }
}
