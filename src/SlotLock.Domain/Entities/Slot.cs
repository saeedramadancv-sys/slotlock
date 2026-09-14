using SlotLock.Domain.Common;

namespace SlotLock.Domain.Entities;

/// <summary>
/// A bookable window on a resource, with a fixed number of seats.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a counter instead of counting bookings.</b> The obvious implementation asks the
/// database how many live bookings a slot already has and inserts one more if there is
/// room. That is a time-of-check to time-of-use race: under concurrency two requests both
/// read the same count, both decide there is room, and both insert. The slot is oversold
/// and no single statement was wrong.
/// </para>
/// <para>
/// <see cref="ReservedCount"/> makes the decision and the write the same operation. The
/// row carries a SQL Server <c>rowversion</c>, so the UPDATE that increments it is emitted
/// as <c>WHERE Id = @id AND RowVersion = @version</c>. If a competing transaction committed
/// first, zero rows match, EF Core raises <c>DbUpdateConcurrencyException</c>, and the
/// loser retries against fresh state instead of overwriting the winner.
/// </para>
/// <para>
/// A CHECK constraint on <c>ReservedCount &lt;= Capacity</c> backs this up in the schema.
/// The domain rule and the retry loop should make it unreachable; it exists so that a bug
/// in either one fails the transaction instead of quietly overselling.
/// </para>
/// <para>
/// <b>Holds count against capacity.</b> <see cref="ReservedCount"/> covers held and
/// confirmed bookings alike. A seat being paid for is not available, and treating it as
/// available is how a system sells the same seat twice during checkout.
/// </para>
/// </remarks>
public class Slot : BaseEntity
{
    // EF Core materialisation constructor.
    private Slot()
    {
        RowVersion = [];
    }

    public Slot(Guid resourceId, DateTimeOffset startUtc, DateTimeOffset endUtc, int capacity, DateTimeOffset now)
    {
        Guard.AgainstInvalidWindow(startUtc, endUtc, nameof(endUtc));

        ResourceId = resourceId;
        StartUtc = startUtc.ToUniversalTime();
        EndUtc = endUtc.ToUniversalTime();
        Capacity = Guard.AgainstNonPositive(capacity, nameof(capacity));
        ReservedCount = 0;
        RowVersion = [];
        StampCreated(now);
    }

    public Guid ResourceId { get; private set; }

    public Resource? Resource { get; private set; }

    public DateTimeOffset StartUtc { get; private set; }

    public DateTimeOffset EndUtc { get; private set; }

    /// <summary>Total seats. A one-to-one resource such as a barber's chair uses 1.</summary>
    public int Capacity { get; private set; }

    /// <summary>Seats currently spoken for, counting both holds and confirmed bookings.</summary>
    public int ReservedCount { get; private set; }

    /// <summary>
    /// SQL Server <c>rowversion</c>. EF Core treats it as a concurrency token, which is
    /// what turns a lost update into a detectable conflict. Never set by application code.
    /// </summary>
    public byte[] RowVersion { get; private set; }

    public int RemainingCapacity => Capacity - ReservedCount;

    public bool IsFull => ReservedCount >= Capacity;

    public bool HasStarted(DateTimeOffset now) => now >= StartUtc;

    /// <summary>
    /// Takes one seat.
    /// </summary>
    /// <exception cref="SlotFullException">The slot is already at capacity.</exception>
    public void Reserve()
    {
        if (IsFull)
        {
            throw new SlotFullException(Id);
        }

        ReservedCount++;
    }

    /// <summary>
    /// Returns one seat, after a cancellation or an expired hold.
    /// </summary>
    /// <remarks>
    /// Guarded against going below zero. A double release would hand out a seat that was
    /// never taken, so the slot would then accept more bookings than it has capacity for -
    /// the same overselling bug, arriving from the other direction.
    /// </remarks>
    public void Release()
    {
        if (ReservedCount == 0)
        {
            throw new DomainException(
                "release_underflow",
                $"Slot {Id} has no reserved seats to release.");
        }

        ReservedCount--;
    }

    /// <summary>
    /// Widens or narrows the slot. Capacity may not drop below what is already reserved:
    /// the alternative is deciding which existing customer loses their seat, and that is
    /// an operator's decision, not a side effect of an edit.
    /// </summary>
    public void ChangeCapacity(int capacity)
    {
        Guard.AgainstNonPositive(capacity, nameof(capacity));

        if (capacity < ReservedCount)
        {
            throw new DomainException(
                "capacity_below_reserved",
                $"Capacity cannot be set to {capacity}; {ReservedCount} seats are already reserved.");
        }

        Capacity = capacity;
    }
}
