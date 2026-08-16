using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>
/// Entity Framework Core configuration for the <see cref="BrokerSession"/> entity.
/// Defines table mapping, keys, constraints, and indexes.
/// </summary>
public class BrokerSessionConfiguration : IEntityTypeConfiguration<BrokerSession>
{
    public void Configure(EntityTypeBuilder<BrokerSession> builder)
    {
        builder.ToTable("broker_sessions");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.BrokerName)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.AccessToken)
            .IsRequired();

        builder.Property(x => x.RefreshToken)
            .IsRequired(false);

        builder.Property(x => x.CreatedUtc)
            .IsRequired();

        builder.Property(x => x.UpdatedUtc)
            .IsRequired();

        builder.Property(x => x.IsActive)
            .IsRequired();

        builder.HasIndex(x => x.BrokerName);
    }
}

