using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations
{
    /// <summary>
    /// Entity Framework Core configuration for the <see cref="SymbolSyncState"/> entity.
    /// Manages the schema for tracking historical data synchronization progress per symbol.
    /// </summary>
    public class SymbolSyncStateConfiguration : IEntityTypeConfiguration<SymbolSyncState>
    {
        public void Configure(EntityTypeBuilder<SymbolSyncState> builder)
        {
            builder.ToTable("symbol_sync_states");

            builder.HasKey(x => x.Id);

            builder.Property(x => x.Symbol).IsRequired().HasMaxLength(100);
            builder.Property(x => x.Resolution).IsRequired().HasMaxLength(20);
            builder.Property(x => x.SyncStatus).IsRequired().HasMaxLength(50);
            builder.Property(x => x.LastError).HasMaxLength(1000);
            builder.Property(x => x.UpdatedUtc).IsRequired();

            builder.HasIndex(x => new { x.Symbol, x.Resolution }).IsUnique();
        }
    }
}
