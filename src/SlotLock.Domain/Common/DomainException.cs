namespace SlotLock.Domain.Common;

/// <summary>
/// A rule of the booking domain was broken. Distinct from <see cref="ArgumentException"/>,
/// which means the caller passed nonsense; a domain exception means the request was
/// well-formed but is not allowed given the current state.
/// </summary>
/// <remarks>
/// <see cref="Code"/> is a stable, machine-readable string. The API maps it to an RFC 9457
/// problem type so a client can branch on the reason without parsing English prose.
/// </remarks>
public class DomainException : Exception
{
    public DomainException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Every seat in the slot is taken. The caller should pick another slot.</summary>
public sealed class SlotFullException : DomainException
{
    public SlotFullException(Guid slotId)
        : base("slot_full", $"Slot {slotId} has no remaining capacity.")
    {
        SlotId = slotId;
    }

    public Guid SlotId { get; }
}

/// <summary>A booking was asked to move to a state its current state does not allow.</summary>
public sealed class InvalidBookingTransitionException : DomainException
{
    public InvalidBookingTransitionException(string from, string to)
        : base("invalid_transition", $"A booking cannot move from {from} to {to}.")
    {
        From = from;
        To = to;
    }

    public string From { get; }

    public string To { get; }
}

/// <summary>The hold window elapsed before the caller confirmed.</summary>
public sealed class HoldExpiredException : DomainException
{
    public HoldExpiredException(Guid bookingId, DateTimeOffset expiredAtUtc)
        : base("hold_expired", $"The hold on booking {bookingId} expired at {expiredAtUtc:O}.")
    {
        BookingId = bookingId;
        ExpiredAtUtc = expiredAtUtc;
    }

    public Guid BookingId { get; }

    public DateTimeOffset ExpiredAtUtc { get; }
}
