namespace SlotLock.Domain.Enums;

/// <summary>
/// Lifecycle of a booking. Stored as a string in the database rather than an int so a
/// row stays readable in a query window during an incident, and so inserting a new
/// member in the middle of this enum can never silently re-label existing rows.
/// </summary>
public enum BookingStatus
{
    /// <summary>
    /// Capacity is reserved but the booking is not final. Holds exist so a caller can
    /// take payment without another caller stealing the seat mid-checkout.
    /// </summary>
    Held = 0,

    /// <summary>Final. The seat belongs to this booking.</summary>
    Confirmed = 1,

    /// <summary>Released by the customer or an operator. Capacity returned to the slot.</summary>
    Cancelled = 2,

    /// <summary>
    /// The hold window elapsed without a confirmation and the sweeper reclaimed the seat.
    /// </summary>
    Expired = 3,
}
