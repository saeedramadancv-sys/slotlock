using SlotLock.Domain.Entities;

namespace SlotLock.Application.Abstractions;

/// <summary>
/// Reads and stages writes for <see cref="Resource"/>.
/// </summary>
/// <remarks>
/// The repositories stage rather than save. Saving is <see cref="IUnitOfWork"/>'s job, so
/// that one call can commit work spanning several of them - notably a booking, a slot, and
/// an outbox message together.
/// </remarks>
public interface IResourceRepository
{
    Task<Resource?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Resource>> ListAsync(CancellationToken cancellationToken = default);

    void Add(Resource resource);
}

public interface ISlotRepository
{
    Task<Slot?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Slots on a resource that overlap the window, with the seats already taken.
    /// </summary>
    /// <remarks>
    /// Overlap, not containment: a caller asking for "this afternoon" wants the 13:30
    /// appointment that runs past 14:00, not only the ones that fit entirely inside.
    /// </remarks>
    Task<IReadOnlyList<Slot>> ListOverlappingAsync(
        Guid resourceId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

    /// <summary>Start times already present on a resource within a window.</summary>
    /// <remarks>
    /// Used to make slot generation repeatable. Running the generator twice over the same
    /// week should add nothing the second time rather than doubling every appointment.
    /// </remarks>
    Task<IReadOnlyList<DateTimeOffset>> ListStartTimesAsync(
        Guid resourceId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

    void Add(Slot slot);

    void AddRange(IEnumerable<Slot> slots);
}

public interface IBookingRepository
{
    Task<Booking?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Held bookings whose hold window has passed, oldest first, capped at
    /// <paramref name="batchSize"/>.
    /// </summary>
    /// <remarks>
    /// Batched on purpose. After an outage the backlog can be large, and loading all of it
    /// would spike memory and hold a transaction open long enough to block live bookings.
    /// The sweeper takes a bounded bite and comes back.
    /// </remarks>
    Task<IReadOnlyList<Booking>> ListLapsedHoldsAsync(
        DateTimeOffset nowUtc,
        int batchSize,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Booking>> ListForSlotAsync(
        Guid slotId,
        CancellationToken cancellationToken = default);

    void Add(Booking booking);
}

public interface IOutboxRepository
{
    void Add(OutboxMessage message);

    /// <summary>Pending messages whose next attempt is due, oldest first.</summary>
    Task<IReadOnlyList<OutboxMessage>> ListDueAsync(
        DateTimeOffset nowUtc,
        int batchSize,
        CancellationToken cancellationToken = default);
}
