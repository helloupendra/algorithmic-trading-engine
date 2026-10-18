using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="PortfolioConnection"/>.</summary>
public class PortfolioConnectionConfiguration : IEntityTypeConfiguration<PortfolioConnection>
{
    public void Configure(EntityTypeBuilder<PortfolioConnection> builder)
    {
        builder.ToTable("portfolio_connections");

        builder.HasKey(x => x.Id);

        // One link per user per broker: a second would leave two tokens for one account and two copies of its holdings.
        builder.HasIndex(x => new { x.UserId, x.Broker }, "IX_portfolio_connections_UserId_Broker").IsUnique();

        builder.Property(x => x.Broker).IsRequired().HasMaxLength(16);
        builder.Property(x => x.Status).IsRequired().HasMaxLength(16);
        builder.Property(x => x.AccountLabel).IsRequired().HasMaxLength(16);
        builder.Property(x => x.TokenProtected).IsRequired();
        builder.Property(x => x.LastError).HasMaxLength(500);
    }
}
