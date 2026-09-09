using AlgoTrading.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlgoTrading.Infrastructure.Persistence.Configurations;

/// <summary>EF configuration for <see cref="Whiteboard"/>.</summary>
public class WhiteboardConfiguration : IEntityTypeConfiguration<Whiteboard>
{
    public void Configure(EntityTypeBuilder<Whiteboard> builder)
    {
        builder.ToTable("whiteboards");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name).IsRequired().HasMaxLength(120);

        // text, not jsonb: the API validates the scene at the boundary and then
        // treats it as an opaque blob it hands straight back. jsonb would parse
        // and re-serialise every multi-megabyte save (embedded images live inside
        // it) for a structure nothing on the server ever queries.
        builder.Property(x => x.SceneJson).IsRequired().HasColumnType("text");

        builder.Property(x => x.Version).IsRequired();
        builder.Property(x => x.CreatedUtc).IsRequired();
        builder.Property(x => x.UpdatedUtc).IsRequired();
        builder.Property(x => x.UpdatedBy).HasMaxLength(100);

        // A board cannot outlive the account that owns it.
        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(x => x.OwnerUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.OwnerUserId);
    }
}
