using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SlotLock.Domain.Entities;

namespace SlotLock.Infrastructure.Persistence.Configurations;

public sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Bookings");

        builder.HasKey(b => b.Id);

        builder.Property(b => b.CustomerReference)
            .IsRequired()
            .HasMaxLength(200);

        // Stored as text. An int would make a production row unreadable without the enum to
        // hand, and would let a member inserted in the middle of the enum silently re-label
        // every existing booking.
        builder.Property(b => b.Status)
            .IsRequired()
            .HasMaxLength(20)
            .HasConversion<string>();

        builder.Property(b => b.HoldExpiresAtUtc).IsRequired();

        builder.HasOne(b => b.Slot)
            .WithMany()
            .HasForeignKey(b => b.SlotId)
            .OnDelete(DeleteBehavior.Cascade);

        // The sweeper's query, and the only one that runs on a timer whether or not anyone
        // is using the service. Status first because it is the selective half: at any moment
        // almost every booking is Confirmed and only a handful are Held.
        builder.HasIndex(b => new { b.Status, b.HoldExpiresAtUtc })
            .HasDatabaseName("IX_Bookings_Status_HoldExpiresAtUtc");

        builder.HasIndex(b => b.SlotId)
            .HasDatabaseName("IX_Bookings_SlotId");

        builder.Ignore(b => b.IsLive);
    }
}
