using FluentAssertions;
using SlotLock.Domain.Common;
using SlotLock.Domain.Entities;

namespace SlotLock.UnitTests.Domain;

/// <summary>
/// Capacity arithmetic. These run in memory: the rule that a slot never hands out more
/// seats than it has is a property of the entity, and it should hold before a database is
/// involved at all. The database-level proof lives in the integration suite.
/// </summary>
public class SlotTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static Slot NewSlot(int capacity = 1) =>
        new(Guid.CreateVersion7(), Now, Now.AddMinutes(30), capacity, Now);

    [Fact]
    public void Reserve_takes_one_seat()
    {
        var slot = NewSlot(capacity: 3);

        slot.Reserve();

        slot.ReservedCount.Should().Be(1);
        slot.RemainingCapacity.Should().Be(2);
        slot.IsFull.Should().BeFalse();
    }

    [Fact]
    public void Reserve_throws_once_capacity_is_exhausted()
    {
        var slot = NewSlot(capacity: 2);
        slot.Reserve();
        slot.Reserve();

        var act = () => slot.Reserve();

        act.Should().Throw<SlotFullException>()
            .Which.SlotId.Should().Be(slot.Id);
        slot.ReservedCount.Should().Be(2, "a refused reservation must not leave a partial effect");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(50)]
    public void Reserving_exactly_capacity_times_fills_the_slot(int capacity)
    {
        var slot = NewSlot(capacity);

        for (var i = 0; i < capacity; i++)
        {
            slot.Reserve();
        }

        slot.IsFull.Should().BeTrue();
        slot.RemainingCapacity.Should().Be(0);
    }

    [Fact]
    public void Release_returns_a_seat()
    {
        var slot = NewSlot(capacity: 1);
        slot.Reserve();

        slot.Release();

        slot.ReservedCount.Should().Be(0);
        slot.IsFull.Should().BeFalse();
    }

    [Fact]
    public void Release_on_an_empty_slot_throws_rather_than_going_negative()
    {
        // A negative count would make RemainingCapacity exceed Capacity, so the slot would
        // start accepting more bookings than it has seats. Overselling, arriving backwards.
        var slot = NewSlot(capacity: 1);

        var act = () => slot.Release();

        act.Should().Throw<DomainException>()
            .Which.Code.Should().Be("release_underflow");
    }

    [Fact]
    public void Capacity_cannot_be_reduced_below_seats_already_taken()
    {
        var slot = NewSlot(capacity: 3);
        slot.Reserve();
        slot.Reserve();

        var act = () => slot.ChangeCapacity(1);

        act.Should().Throw<DomainException>()
            .Which.Code.Should().Be("capacity_below_reserved");
        slot.Capacity.Should().Be(3);
    }

    [Fact]
    public void Capacity_can_be_reduced_to_exactly_what_is_reserved()
    {
        var slot = NewSlot(capacity: 5);
        slot.Reserve();
        slot.Reserve();

        slot.ChangeCapacity(2);

        slot.Capacity.Should().Be(2);
        slot.IsFull.Should().BeTrue();
    }

    [Fact]
    public void A_slot_that_ends_before_it_starts_is_rejected()
    {
        var act = () => new Slot(Guid.CreateVersion7(), Now, Now.AddMinutes(-1), 1, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_zero_length_slot_is_rejected()
    {
        // It would be bookable while occupying no time, which breaks every overlap and
        // availability calculation downstream.
        var act = () => new Slot(Guid.CreateVersion7(), Now, Now, 1, Now);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Capacity_must_be_positive()
    {
        var act = () => new Slot(Guid.CreateVersion7(), Now, Now.AddMinutes(30), 0, Now);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Times_are_normalised_to_utc()
    {
        // Callers send offsets from their own region. Storing the offset as given would make
        // two slots at the same instant compare unequal depending on who created them.
        var amman = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(3));

        var slot = new Slot(Guid.CreateVersion7(), amman, amman.AddMinutes(30), 1, Now);

        slot.StartUtc.Offset.Should().Be(TimeSpan.Zero);
        slot.StartUtc.Hour.Should().Be(9);
    }
}
