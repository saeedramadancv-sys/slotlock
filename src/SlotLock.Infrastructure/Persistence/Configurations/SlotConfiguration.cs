using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SlotLock.Domain.Entities;

namespace SlotLock.Infrastructure.Persistence.Configurations;

/// <summary>
/// Table mapping for <see cref="Slot"/> - the row every concurrent booking fights over.
/// </summary>
public sealed class SlotConfiguration : IEntityTypeConfiguration<Slot>
{
    public void Configure(EntityTypeBuilder<Slot> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Slots", t => t.HasCheckConstraint(
            "CK_Slots_ReservedWithinCapacity",
            "[ReservedCount] >= 0 AND [ReservedCount] <= [Capacity]"));

        builder.HasKey(s => s.Id);

        builder.Property(s => s.StartUtc).IsRequired();
        builder.Property(s => s.EndUtc).IsRequired();
        builder.Property(s => s.Capacity).IsRequired();
        builder.Property(s => s.ReservedCount).IsRequired();

        // The concurrency token. SQL Server maintains rowversion itself on every UPDATE, so
        // EF emits "WHERE Id = @id AND RowVersion = @version" and a write built on stale
        // state matches zero rows. That mismatch is what becomes DbUpdateConcurrencyException,
        // and in turn what the retry policy reacts to.
        //
        // Worth stating plainly: without this line every other layer of the overselling
        // defence still compiles, still passes a single-threaded test, and still oversells
        // under load.
        builder.Property(s => s.RowVersion)
            .IsRowVersion()
            .IsConcurrencyToken();

        builder.HasOne(s => s.Resource)
            .WithMany(r => r.Slots)
            .HasForeignKey(s => s.ResourceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Availability always asks "this resource, this window", and slot generation checks
        // for existing start times the same way. Leading on ResourceId and ordering by
        // StartUtc lets both be a range seek rather than a scan of every slot ever created.
        builder.HasIndex(s => new { s.ResourceId, s.StartUtc })
            .HasDatabaseName("IX_Slots_ResourceId_StartUtc");

        builder.Ignore(s => s.RemainingCapacity);
        builder.Ignore(s => s.IsFull);
    }
}
