using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SlotLock.Domain.Entities;

namespace SlotLock.Infrastructure.Persistence.Configurations;

public sealed class ResourceConfiguration : IEntityTypeConfiguration<Resource>
{
    public void Configure(EntityTypeBuilder<Resource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Resources");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Name).IsRequired().HasMaxLength(120);

        // Long enough for any IANA id; "America/Argentina/ComodRivadavia" is 31 characters.
        builder.Property(r => r.TimeZoneId).IsRequired().HasMaxLength(64);

        builder.Property(r => r.DefaultCapacity).IsRequired();
        builder.Property(r => r.IsActive).IsRequired();

        builder.Metadata
            .FindNavigation(nameof(Resource.Slots))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("OutboxMessages");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.Type).IsRequired().HasMaxLength(200);
        builder.Property(m => m.Payload).IsRequired();
        builder.Property(m => m.Status).IsRequired().HasMaxLength(20).HasConversion<string>();
        builder.Property(m => m.Attempts).IsRequired();
        builder.Property(m => m.NextAttemptAtUtc).IsRequired();
        builder.Property(m => m.LastError).HasMaxLength(2000);

        // The dispatcher polls this every few seconds for the lifetime of the service. A
        // filtered index keeps it to the rows that can actually be picked up: processed and
        // dead-lettered messages accumulate forever and would otherwise bloat the index that
        // the hottest recurring query depends on.
        builder.HasIndex(m => new { m.Status, m.NextAttemptAtUtc })
            .HasDatabaseName("IX_OutboxMessages_Pending")
            .HasFilter("[Status] = 'Pending'");
    }
}

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("IdempotencyRecords");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Key).IsRequired().HasMaxLength(200);
        builder.Property(r => r.Endpoint).IsRequired().HasMaxLength(200);

        // SHA-256, hex encoded.
        builder.Property(r => r.RequestFingerprint).IsRequired().HasMaxLength(64);

        builder.Property(r => r.ResponseBody).HasMaxLength(8000);
        builder.Property(r => r.ExpiresAtUtc).IsRequired();

        // This unique index is the mechanism, not a safety net. Claiming a key is an INSERT;
        // when two retries race, exactly one insert succeeds and the other gets a duplicate-key
        // violation, which the store reads as "somebody else already owns this request".
        // Checking for the row in application code first would reintroduce the very gap the
        // key exists to close.
        builder.HasIndex(r => new { r.Key, r.Endpoint })
            .IsUnique()
            .HasDatabaseName("UX_IdempotencyRecords_Key_Endpoint");

        builder.HasIndex(r => r.ExpiresAtUtc)
            .HasDatabaseName("IX_IdempotencyRecords_ExpiresAtUtc");

        builder.Ignore(r => r.IsCompleted);
    }
}
