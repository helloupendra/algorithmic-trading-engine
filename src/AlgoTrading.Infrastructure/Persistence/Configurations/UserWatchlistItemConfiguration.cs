using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

public class UserWatchlistItemConfiguration : IEntityTypeConfiguration<UserWatchlistItem>
{
    public void Configure(EntityTypeBuilder<UserWatchlistItem> builder)
    {
        builder.ToTable("user_watchlist_items");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Symbol).IsRequired().HasMaxLength(100);

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.UserId, x.Symbol }).IsUnique();
    }
}
