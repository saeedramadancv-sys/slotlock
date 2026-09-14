using SlotLock.Domain.Entities;
using SlotLock.Domain.Enums;

namespace SlotLock.Application.Bookings;

/// <summary>
/// A booking as the API returns it.
/// </summary>
/// <remarks>
/// Separate from the entity on purpose. Serialising <see cref="Booking"/> directly would put
/// its navigation properties and its private-setter shape into the public contract, so a
/// refactor inside the domain would silently become a breaking API change.
/// </remarks>
public sealed record BookingDto(
    Guid Id,
    Guid SlotId,
    string CustomerReference,
    BookingStatus Status,
    DateTimeOffset SlotStartUtc,
    DateTimeOffset SlotEndUtc,
    DateTimeOffset? HoldExpiresAtUtc,
    DateTimeOffset? ConfirmedAtUtc,
    DateTimeOffset CreatedAtUtc)
{
    public static BookingDto From(Booking booking, Slot slot) => new(
        booking.Id,
        booking.SlotId,
        booking.CustomerReference,
        booking.Status,
        slot.StartUtc,
        slot.EndUtc,
        // Only meaningful while the booking is held; sending a stale deadline alongside a
        // confirmed booking invites a client to display a countdown that means nothing.
        booking.Status == BookingStatus.Held ? booking.HoldExpiresAtUtc : null,
        booking.ConfirmedAtUtc,
        booking.CreatedAtUtc);
}

/// <summary>One slot and how much of it is left.</summary>
public sealed record SlotAvailabilityDto(
    Guid SlotId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    int Capacity,
    int Remaining)
{
    public static SlotAvailabilityDto From(Slot slot) =>
        new(slot.Id, slot.StartUtc, slot.EndUtc, slot.Capacity, slot.RemainingCapacity);
}

public sealed record ResourceDto(
    Guid Id,
    string Name,
    string TimeZoneId,
    int DefaultCapacity,
    bool IsActive)
{
    public static ResourceDto From(Resource resource) =>
        new(resource.Id, resource.Name, resource.TimeZoneId, resource.DefaultCapacity, resource.IsActive);
}
