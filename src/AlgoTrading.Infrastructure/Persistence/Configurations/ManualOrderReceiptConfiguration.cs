using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

public sealed class ManualOrderReceiptConfiguration : IEntityTypeConfiguration<ManualOrderReceipt>
{
    public void Configure(EntityTypeBuilder<ManualOrderReceipt> builder)
    {
        builder.ToTable("manual_order_receipts");
        builder.HasKey(x => new { x.UserId, x.ClientOrderId }).HasName("pk_manual_order_receipts");
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.FillJson).HasColumnType("jsonb").IsRequired();
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
