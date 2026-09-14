using Microsoft.EntityFrameworkCore;
using SlotLock.Domain.Entities;

namespace SlotLock.Infrastructure.Persistence;

/// <summary>
/// The EF Core model. Mapping lives in <c>IEntityTypeConfiguration</c> classes under
/// <c>Persistence/Configurations</c> rather than in one long <c>OnModelCreating</c>, so each
/// table's rules sit next to each other.
/// </summary>
public class SlotLockDbContext : DbContext
{
    public SlotLockDbContext(DbContextOptions<SlotLockDbContext> options) : base(options)
    {
    }

    public DbSet<Resource> Resources => Set<Resource>();

    public DbSet<Slot> Slots => Set<Slot>();

    public DbSet<Booking> Bookings => Set<Booking>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SlotLockDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
