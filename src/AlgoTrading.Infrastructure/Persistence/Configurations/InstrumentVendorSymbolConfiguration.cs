using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="InstrumentVendorSymbol"/>.</summary>
public class InstrumentVendorSymbolConfiguration : IEntityTypeConfiguration<InstrumentVendorSymbol>
{
    public void Configure(EntityTypeBuilder<InstrumentVendorSymbol> builder)
    {
        builder.ToTable("instrument_vendor_symbols");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProviderKey)
            .IsRequired()
            .HasMaxLength(32);

        builder.Property(x => x.CanonicalSymbol)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.VendorSymbol)
            .IsRequired()
            .HasMaxLength(100);

        // Both directions must be unambiguous: one vendor symbol per instrument,
        // and one instrument per vendor symbol.
        builder.HasIndex(x => new { x.ProviderKey, x.CanonicalSymbol }).IsUnique();
        builder.HasIndex(x => new { x.ProviderKey, x.VendorSymbol }).IsUnique();
    }
}
