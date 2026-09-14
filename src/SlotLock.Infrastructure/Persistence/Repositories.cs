using Microsoft.EntityFrameworkCore;
using SlotLock.Application.Abstractions;
using SlotLock.Domain.Entities;
using SlotLock.Domain.Enums;

namespace SlotLock.Infrastructure.Persistence;

/// <summary>
/// EF Core implementations of the application's repository abstractions.
/// </summary>
/// <remarks>
/// They stage; they never save. Every write path in this service needs several entities to
/// land in one transaction - a booking, its slot, its outbox row - so committing inside a
/// repository would break the guarantee the whole design rests on.
/// </remarks>
public sealed class ResourceRepository : IResourceRepository
{
    private readonly SlotLockDbContext _context;

    public ResourceRepository(SlotLockDbContext context) => _context = context;

    public Task<Resource?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Resources.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Resource>> ListAsync(CancellationToken cancellationToken = default) =>
        await _context.Resources
            .AsNoTracking()
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void Add(Resource resource) => _context.Resources.Add(resource);
}

public sealed class SlotRepository : ISlotRepository
{
    private readonly SlotLockDbContext _context;

    public SlotRepository(SlotLockDbContext context) => _context = context;

    /// <summary>
    /// Loads a slot for update.
    /// </summary>
    /// <remarks>
    /// Tracked deliberately. The booking path mutates the seat count and relies on EF issuing
    /// an UPDATE that carries the RowVersion it read; an untracked entity produces no such
    /// statement, and the concurrency check silently does not happen.
    /// </remarks>
    public Task<Slot?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Slots.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Slot>> ListOverlappingAsync(
        Guid resourceId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default) =>
        await _context.Slots
            .AsNoTracking()
            .Where(s => s.ResourceId == resourceId && s.StartUtc < toUtc && s.EndUtc > fromUtc)
            .OrderBy(s => s.StartUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<DateTimeOffset>> ListStartTimesAsync(
        Guid resourceId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default) =>
        await _context.Slots
            .AsNoTracking()
            .Where(s => s.ResourceId == resourceId && s.StartUtc >= fromUtc && s.StartUtc < toUtc)
            // Projection, not whole entities: generation only needs to know which start times
            // are taken, and materialising a quarter of slots to read one column each is a
            // lot of memory for a set membership test.
            .Select(s => s.StartUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void Add(Slot slot) => _context.Slots.Add(slot);

    public void AddRange(IEnumerable<Slot> slots) => _context.Slots.AddRange(slots);
}

public sealed class BookingRepository : IBookingRepository
{
    private readonly SlotLockDbContext _context;

    public BookingRepository(SlotLockDbContext context) => _context = context;

    public Task<Booking?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Bookings.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Booking>> ListLapsedHoldsAsync(
        DateTimeOffset nowUtc,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        await _context.Bookings
            .Where(b => b.Status == BookingStatus.Held && b.HoldExpiresAtUtc < nowUtc)
            // Oldest first, so a backlog drains in the order seats were owed back rather than
            // leaving the earliest holds stranded behind newer ones.
            .OrderBy(b => b.HoldExpiresAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<Booking>> ListForSlotAsync(
        Guid slotId,
        CancellationToken cancellationToken = default) =>
        await _context.Bookings
            .AsNoTracking()
            .Where(b => b.SlotId == slotId)
            .OrderBy(b => b.CreatedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public void Add(Booking booking) => _context.Bookings.Add(booking);
}

public sealed class OutboxRepository : IOutboxRepository
{
    private readonly SlotLockDbContext _context;

    public OutboxRepository(SlotLockDbContext context) => _context = context;

    public void Add(OutboxMessage message) => _context.OutboxMessages.Add(message);

    public async Task<IReadOnlyList<OutboxMessage>> ListDueAsync(
        DateTimeOffset nowUtc,
        int batchSize,
        CancellationToken cancellationToken = default) =>
        await _context.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending && m.NextAttemptAtUtc <= nowUtc)
            .OrderBy(m => m.CreatedAtUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
}
