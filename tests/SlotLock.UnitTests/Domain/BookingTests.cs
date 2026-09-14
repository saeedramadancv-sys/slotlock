using FluentAssertions;
using SlotLock.Domain.Common;
using SlotLock.Domain.Entities;
using SlotLock.Domain.Enums;

namespace SlotLock.UnitTests.Domain;

/// <summary>
/// The booking state machine. Time is passed in rather than read from the clock, so the
/// expiry rules are exercised directly instead of by making the test wait.
/// </summary>
public class BookingTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Hold = TimeSpan.FromMinutes(10);

    private static Booking NewHold() => Booking.Hold(Guid.CreateVersion7(), "customer@example.com", Now, Hold);

    [Fact]
    public void A_new_booking_is_held_not_confirmed()
    {
        var booking = NewHold();

        booking.Status.Should().Be(BookingStatus.Held);
        booking.HoldExpiresAtUtc.Should().Be(Now.Add(Hold));
        booking.ConfirmedAtUtc.Should().BeNull();
        booking.IsLive.Should().BeTrue();
    }

    [Fact]
    public void Confirm_within_the_hold_window_succeeds()
    {
        var booking = NewHold();

        booking.Confirm(Now.AddMinutes(9));

        booking.Status.Should().Be(BookingStatus.Confirmed);
        booking.ConfirmedAtUtc.Should().Be(Now.AddMinutes(9));
    }

    [Fact]
    public void Confirm_at_the_exact_expiry_instant_succeeds()
    {
        // The boundary is inclusive. Picking the other side would mean a client that
        // confirms at the deadline loses the seat to a rounding decision.
        var booking = NewHold();

        booking.Confirm(Now.Add(Hold));

        booking.Status.Should().Be(BookingStatus.Confirmed);
    }

    [Fact]
    public void Confirm_after_the_hold_lapsed_is_refused_even_if_the_sweeper_has_not_run()
    {
        // Otherwise the outcome depends on how recently a background job happened to fire,
        // which is not a rule a caller can reason about.
        var booking = NewHold();

        var act = () => booking.Confirm(Now.Add(Hold).AddSeconds(1));

        act.Should().Throw<HoldExpiredException>()
            .Which.BookingId.Should().Be(booking.Id);
        booking.Status.Should().Be(BookingStatus.Held, "a refused confirmation changes nothing");
    }

    [Fact]
    public void Confirming_twice_is_a_no_op_rather_than_an_error()
    {
        // A retried confirmation should not punish the caller; the outcome is already what
        // they asked for.
        var booking = NewHold();
        booking.Confirm(Now.AddMinutes(1));
        var firstConfirmedAt = booking.ConfirmedAtUtc;

        booking.Confirm(Now.AddMinutes(2));

        booking.Status.Should().Be(BookingStatus.Confirmed);
        booking.ConfirmedAtUtc.Should().Be(firstConfirmedAt, "the original confirmation time stands");
    }

    [Fact]
    public void Cancel_reports_whether_it_changed_anything()
    {
        var booking = NewHold();

        booking.Cancel(Now.AddMinutes(1)).Should().BeTrue();
        booking.Status.Should().Be(BookingStatus.Cancelled);

        // The second call must return false: the caller uses this to decide whether to give
        // a seat back to the slot, and a double release oversells it.
        booking.Cancel(Now.AddMinutes(2)).Should().BeFalse();
    }

    [Fact]
    public void A_confirmed_booking_can_still_be_cancelled()
    {
        var booking = NewHold();
        booking.Confirm(Now.AddMinutes(1));

        booking.Cancel(Now.AddMinutes(5)).Should().BeTrue();

        booking.Status.Should().Be(BookingStatus.Cancelled);
        booking.IsLive.Should().BeFalse();
    }

    [Fact]
    public void A_cancelled_booking_cannot_be_confirmed()
    {
        var booking = NewHold();
        booking.Cancel(Now.AddMinutes(1));

        var act = () => booking.Confirm(Now.AddMinutes(2));

        act.Should().Throw<InvalidBookingTransitionException>();
    }

    [Fact]
    public void Expire_applies_only_after_the_hold_lapsed()
    {
        var booking = NewHold();

        var tooEarly = () => booking.Expire(Now.AddMinutes(5));

        tooEarly.Should().Throw<DomainException>()
            .Which.Code.Should().Be("hold_still_valid");

        booking.Expire(Now.Add(Hold).AddSeconds(1)).Should().BeTrue();
        booking.Status.Should().Be(BookingStatus.Expired);
    }

    [Fact]
    public void Expire_does_not_touch_a_confirmed_booking()
    {
        // The sweeper runs on a schedule and will meet bookings confirmed since it last
        // queried. Expiring one would cancel a paid seat.
        var booking = NewHold();
        booking.Confirm(Now.AddMinutes(1));

        booking.Expire(Now.AddHours(1)).Should().BeFalse();

        booking.Status.Should().Be(BookingStatus.Confirmed);
    }

    [Fact]
    public void HoldHasLapsed_is_false_for_anything_that_is_not_held()
    {
        var booking = NewHold();
        booking.Confirm(Now.AddMinutes(1));

        booking.HoldHasLapsed(Now.AddDays(1)).Should().BeFalse();
    }

    [Fact]
    public void A_hold_must_last_a_positive_amount_of_time()
    {
        var act = () => Booking.Hold(Guid.CreateVersion7(), "c", Now, TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_booking_needs_a_customer_reference()
    {
        var act = () => Booking.Hold(Guid.CreateVersion7(), "   ", Now, Hold);

        act.Should().Throw<ArgumentException>();
    }
}
