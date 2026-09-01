using System;
using System.Collections.Generic;
using System.Text;
using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations
{
    public class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
    {
        public void Configure(EntityTypeBuilder<AppUser> builder)
        {
            builder.ToTable("app_user");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.UserName)
                .IsRequired()
                .HasMaxLength(100);

            builder.Property(x => x.Email)
                .IsRequired()
                .HasMaxLength(200);

            builder.Property(x => x.PasswordHash)
                .IsRequired()
                .HasColumnType("text");

            builder.Property(x => x.Role)
                .IsRequired()
                .HasMaxLength(50)
                .HasDefaultValue(Domain.Constants.UserRoles.Trader);

            builder.Property(x => x.CreatedUtc).IsRequired();
            builder.Property(x => x.UpdatedUtc).IsRequired();

            builder.HasIndex(x => x.UserName).IsUnique();
            builder.HasIndex(x => x.Email).IsUnique();

        }
    }
}
