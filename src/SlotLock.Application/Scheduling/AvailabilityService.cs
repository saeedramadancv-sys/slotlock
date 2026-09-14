using Microsoft.Extensions.Options;
using SlotLock.Application.Abstractions;
using SlotLock.Application.Bookings;
using SlotLock.Application.Common;
using SlotLock.Application.Options;
using SlotLock.Domain.Common;
using SlotLock.Domain.Entities;

namespace SlotLock.Application.Scheduling;

/// <summary>
/// Answers "what is free between these two instants".
/// </summary>
/// <remarks>
/// <para>
/// Availability is read straight from each slot's seat count. There is no separate
/// availability table to keep in step, because a second source of truth for the same fact is
/// a source of truth that will eventually disagree.
/// </para>
/// <para>
/// The numbers here are a snapshot and the endpoint says so. Between this response and the
/// customer pressing Book, someone else may take the seat; that race is settled by
/// <see cref="Bookings.BookingService"/>, not by making this query stricter. A read that
/// tried to reserve what it reported would lock a resource for every browser that idly
/// looked at next week.
/// </para>
/// </remarks>
public sealed class AvailabilityService
{
    private readonly ISlotRepository _slots;
    private readonly IResourceRepository _resources;
    private readonly BookingOptions _options;

    public AvailabilityService(
        ISlotRepository slots,
        IResourceRepository resources,
        IOptions<BookingOptions> options)
    {
        _slots = slots;
        _resources = resources;
        _options = options.Value;
    }

    /// <param name="onlyBookable">
    /// When true, slots with no seats left are left out entirely. When false they are
    /// returned with a remaining count of zero, which is what a calendar view needs in order
    /// to draw the day as full rather than as empty.
    /// </param>
    public async Task<IReadOnlyList<SlotAvailabilityDto>> GetAsync(
        Guid resourceId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        bool onlyBookable = true,
        CancellationToken cancellationToken = default)
    {
        if (toUtc <= fromUtc)
        {
            throw new DomainException("invalid_range", "The window must end after it starts.");
        }

        if (toUtc - fromUtc > _options.MaxAvailabilityWindow)
        {
            throw new DomainException(
                "range_too_wide",
                $"Ask for at most {_options.MaxAvailabilityWindow.TotalDays:0} days at a time.");
        }

        _ = await _resources.FindAsync(resourceId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException(nameof(Resource), resourceId);

        var slots = await _slots
            .ListOverlappingAsync(resourceId, fromUtc, toUtc, cancellationToken)
            .ConfigureAwait(false);

        var query = slots.AsEnumerable();

        if (onlyBookable)
        {
            query = query.Where(s => !s.IsFull);
        }

        return [.. query.Select(SlotAvailabilityDto.From)];
    }
}
