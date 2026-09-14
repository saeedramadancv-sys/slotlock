using SlotLock.Domain.Common;
using SlotLock.Domain.Enums;

namespace SlotLock.Domain.Entities;

/// <summary>
/// One customer's claim on a seat in a <see cref="Slot"/>.
/// </summary>
/// <remarks>
/// <para>
/// A booking starts <see cref="BookingStatus.Held"/> rather than confirmed. Checkout takes
/// time - a payment page, a 3-D Secure redirect - and during that time the seat has to be
/// unavailable to everyone else while still being reclaimable if the customer walks away.
/// The hold does both: it counts against the slot's capacity immediately, and it carries an
/// expiry the sweeper acts on.
/// </para>
/// <para>
/// Every transition is checked here rather than in the service layer, so there is exactly
/// one place that decides whether a move is legal and exactly one place to test.
/// </para>
/// </remarks>
public class Booking : BaseEntity
{
    // EF Core materialisation constructor.
    private Booking()
    {
        CustomerReference = string.Empty;
    }

    private Booking(Guid slotId, string customerReference, DateTimeOffset now, TimeSpan holdDuration)
    {
        SlotId = slotId;
        CustomerReference = Guard.AgainstTooLong(
            Guard.AgainstNullOrWhiteSpace(customerReference, nameof(customerReference)),
            200,
            nameof(customerReference));
        Status = BookingStatus.Held;
        HoldExpiresAtUtc = now.Add(holdDuration);
        StampCreated(now);
    }

    /// <summary>
    /// Creates a held booking. Named rather than a public constructor because "hold" is the
    /// domain's word for what happens, and a plain <c>new Booking(...)</c> reads as if the
    /// seat were already final.
    /// </summary>
    public static Booking Hold(Guid slotId, string customerReference, DateTimeOffset now, TimeSpan holdDuration)
    {
        if (holdDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(holdDuration), holdDuration, "A hold must last a positive amount of time.");
        }

        return new Booking(slotId, customerReference, now, holdDuration);
    }

    public Guid SlotId { get; private set; }

    public Slot? Slot { get; private set; }

    /// <summary>
    /// Whoever the seat is for, as the caller identifies them - an email, a tenant's own
    /// customer id. Opaque on purpose: this service does not own customer records.
    /// </summary>
    public string CustomerReference { get; private set; }

    public BookingStatus Status { get; private set; }

    /// <summary>When the hold lapses. Meaningless once the booking leaves <c>Held</c>.</summary>
    public DateTimeOffset HoldExpiresAtUtc { get; private set; }

    public DateTimeOffset? ConfirmedAtUtc { get; private set; }

    public DateTimeOffset? CancelledAtUtc { get; private set; }

    public bool IsLive => Status is BookingStatus.Held or BookingStatus.Confirmed;

    public bool HoldHasLapsed(DateTimeOffset now) => Status == BookingStatus.Held && now > HoldExpiresAtUtc;

    /// <summary>
    /// Makes the seat final.
    /// </summary>
    /// <remarks>
    /// Confirming after the hold lapsed is refused even when the sweeper has not run yet.
    /// Otherwise the outcome would depend on how recently a background job happened to
    /// fire, which is not a rule anyone can reason about or test.
    /// </remarks>
    /// <exception cref="HoldExpiredException">The hold window has passed.</exception>
    /// <exception cref="InvalidBookingTransitionException">The booking is not held.</exception>
    public void Confirm(DateTimeOffset now)
    {
        if (Status == BookingStatus.Confirmed)
        {
            return; // Idempotent: confirming twice is the same outcome, not an error.
        }

        if (Status != BookingStatus.Held)
        {
            throw new InvalidBookingTransitionException(Status.ToString(), nameof(BookingStatus.Confirmed));
        }

        if (now > HoldExpiresAtUtc)
        {
            throw new HoldExpiredException(Id, HoldExpiresAtUtc);
        }

        Status = BookingStatus.Confirmed;
        ConfirmedAtUtc = now;
    }

    /// <summary>
    /// Releases the seat. Legal from both <c>Held</c> and <c>Confirmed</c>.
    /// </summary>
    /// <returns>
    /// True when this call changed the state, so the caller knows whether to return a seat
    /// to the slot. Cancelling an already-cancelled booking returns false and must not
    /// release a second seat.
    /// </returns>
    public bool Cancel(DateTimeOffset now)
    {
        if (Status is BookingStatus.Cancelled or BookingStatus.Expired)
        {
            return false;
        }

        Status = BookingStatus.Cancelled;
        CancelledAtUtc = now;
        return true;
    }

    /// <summary>
    /// Marks a lapsed hold as expired. Called by the sweeper, never by a request.
    /// </summary>
    /// <returns>True when this call changed the state.</returns>
    public bool Expire(DateTimeOffset now)
    {
        if (Status != BookingStatus.Held)
        {
            return false;
        }

        if (now <= HoldExpiresAtUtc)
        {
            throw new DomainException(
                "hold_still_valid",
                $"Booking {Id} cannot expire before {HoldExpiresAtUtc:O}.");
        }

        Status = BookingStatus.Expired;
        return true;
    }
}
