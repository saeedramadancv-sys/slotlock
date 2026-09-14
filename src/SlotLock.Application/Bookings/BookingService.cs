using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlotLock.Application.Abstractions;
using SlotLock.Application.Common;
using SlotLock.Application.Options;
using SlotLock.Domain.Common;
using SlotLock.Domain.Entities;

namespace SlotLock.Application.Bookings;

/// <summary>
/// Holds, confirms and cancels seats.
/// </summary>
/// <remarks>
/// <para>
/// <b>The problem this type exists to solve.</b> Two customers press Book on the last seat
/// at the same moment. Both requests read a slot with one seat free, both decide they may
/// have it, and both write. Nothing in either request is wrong in isolation; the slot is
/// oversold by the interleaving.
/// </para>
/// <para>
/// The defence is three layers, deepest last:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///     <b>The seat count lives on the slot row.</b> Deciding and writing are the same
///     operation, so there is no gap between checking and acting.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>A concurrency token on that row.</b> The UPDATE carries the version it read, so a
///     write built on stale state matches zero rows and is reported instead of applied. The
///     loser retries against fresh state via <see cref="ConcurrencyRetryPolicy"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>A CHECK constraint in the schema.</b> If the two layers above are ever wrong, the
///     transaction fails rather than the slot overselling. It should be unreachable; it is
///     there because "should be" is not a guarantee.
///     </description>
///   </item>
/// </list>
/// <para>
/// No pessimistic lock is taken. Holding a row lock across a request serialises every
/// booking on a resource and turns a popular slot into a queue; the optimistic path lets
/// uncontended bookings run in parallel and pays a retry only when there is an actual race.
/// </para>
/// </remarks>
public sealed class BookingService
{
    private readonly IBookingRepository _bookings;
    private readonly ISlotRepository _slots;
    private readonly IResourceRepository _resources;
    private readonly IOutboxRepository _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ConcurrencyRetryPolicy _retry;
    private readonly TimeProvider _time;
    private readonly BookingOptions _options;
    private readonly ILogger<BookingService> _logger;

    public BookingService(
        IBookingRepository bookings,
        ISlotRepository slots,
        IResourceRepository resources,
        IOutboxRepository outbox,
        IUnitOfWork unitOfWork,
        ConcurrencyRetryPolicy retry,
        TimeProvider time,
        IOptions<BookingOptions> options,
        ILogger<BookingService> logger)
    {
        _bookings = bookings;
        _slots = slots;
        _resources = resources;
        _outbox = outbox;
        _unitOfWork = unitOfWork;
        _retry = retry;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Reserves a seat and returns a held booking.
    /// </summary>
    /// <exception cref="NotFoundException">No such slot.</exception>
    /// <exception cref="SlotFullException">Every seat is taken.</exception>
    /// <exception cref="ConcurrencyExhaustedException">
    /// The slot stayed contested across every retry.
    /// </exception>
    public Task<BookingDto> HoldAsync(
        Guid slotId,
        string customerReference,
        CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync(
            async (attempt, ct) =>
            {
                // Attempt 2 onwards must not reuse what attempt 1 loaded: the whole point of
                // retrying is to see the winner's committed state.
                if (attempt > 1)
                {
                    _unitOfWork.Reset();
                }

                var now = _time.GetUtcNow();
                var slot = await _slots.FindAsync(slotId, ct).ConfigureAwait(false)
                    ?? throw new NotFoundException(nameof(Slot), slotId);

                var resource = await _resources.FindAsync(slot.ResourceId, ct).ConfigureAwait(false)
                    ?? throw new NotFoundException(nameof(Resource), slot.ResourceId);

                if (!resource.IsActive)
                {
                    throw new DomainException(
                        "resource_inactive",
                        $"Resource '{resource.Name}' is not accepting bookings.");
                }

                if (slot.HasStarted(now))
                {
                    // Booking a slot that has already begun is almost always a client working
                    // from a cached availability page, not a customer who wants half a session.
                    throw new DomainException(
                        "slot_started",
                        $"Slot {slot.Id} started at {slot.StartUtc:O} and can no longer be booked.");
                }

                // Throws SlotFullException when the slot is full. Deliberately not caught and
                // retried: a full slot is an answer, not a race, and retrying would only make
                // the client wait longer for the same 409.
                slot.Reserve();

                var booking = Booking.Hold(slot.Id, customerReference, now, _options.HoldDuration);
                _bookings.Add(booking);

                // Written in the same transaction as the booking. If the commit rolls back,
                // the notification rolls back with it, so nobody is told about a seat they
                // do not have.
                _outbox.Add(Event("booking.held", booking, slot, now));

                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

                _logger.LogInformation(
                    "Held booking {BookingId} on slot {SlotId} ({Reserved}/{Capacity} seats taken) after {Attempts} attempt(s).",
                    booking.Id,
                    slot.Id,
                    slot.ReservedCount,
                    slot.Capacity,
                    attempt);

                return BookingDto.From(booking, slot);
            },
            cancellationToken);

    /// <summary>
    /// Makes a held seat final.
    /// </summary>
    /// <remarks>
    /// The slot row is untouched: the seat was already counted when the hold was taken. That
    /// is the point of counting holds against capacity - confirmation is a state change on
    /// the booking, not a second competition for the seat.
    /// </remarks>
    public Task<BookingDto> ConfirmAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync(
            async (attempt, ct) =>
            {
                if (attempt > 1)
                {
                    _unitOfWork.Reset();
                }

                var now = _time.GetUtcNow();
                var booking = await _bookings.FindAsync(bookingId, ct).ConfigureAwait(false)
                    ?? throw new NotFoundException(nameof(Booking), bookingId);

                var slot = await _slots.FindAsync(booking.SlotId, ct).ConfigureAwait(false)
                    ?? throw new NotFoundException(nameof(Slot), booking.SlotId);

                var wasAlreadyConfirmed = booking.Status == Domain.Enums.BookingStatus.Confirmed;

                booking.Confirm(now);

                // A replayed confirmation must not queue a second confirmation email.
                if (!wasAlreadyConfirmed)
                {
                    _outbox.Add(Event("booking.confirmed", booking, slot, now));
                }

                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

                return BookingDto.From(booking, slot);
            },
            cancellationToken);

    /// <summary>
    /// Releases a seat, whether the booking was held or confirmed.
    /// </summary>
    public Task<BookingDto> CancelAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync(
            async (attempt, ct) =>
            {
                if (attempt > 1)
                {
                    _unitOfWork.Reset();
                }

                var now = _time.GetUtcNow();
                var booking = await _bookings.FindAsync(bookingId, ct).ConfigureAwait(false)
                    ?? throw new NotFoundException(nameof(Booking), bookingId);

                var slot = await _slots.FindAsync(booking.SlotId, ct).ConfigureAwait(false)
                    ?? throw new NotFoundException(nameof(Slot), booking.SlotId);

                // Cancel reports whether it actually changed anything. Releasing a seat for an
                // already-cancelled booking would hand out capacity that was never taken -
                // overselling by way of a double refund.
                if (booking.Cancel(now))
                {
                    slot.Release();
                    _outbox.Add(Event("booking.cancelled", booking, slot, now));
                    await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

                    _logger.LogInformation(
                        "Cancelled booking {BookingId}; slot {SlotId} now {Reserved}/{Capacity}.",
                        booking.Id,
                        slot.Id,
                        slot.ReservedCount,
                        slot.Capacity);
                }

                return BookingDto.From(booking, slot);
            },
            cancellationToken);

    public async Task<BookingDto> GetAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await _bookings.FindAsync(bookingId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException(nameof(Booking), bookingId);

        var slot = await _slots.FindAsync(booking.SlotId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException(nameof(Slot), booking.SlotId);

        return BookingDto.From(booking, slot);
    }

    /// <summary>
    /// Builds the outbox row for a booking event.
    /// </summary>
    /// <remarks>
    /// The payload carries the booking id as the message identity. Outbox delivery is
    /// at-least-once, so a consumer needs something stable to recognise a repeat by.
    /// </remarks>
    private static OutboxMessage Event(string type, Booking booking, Slot slot, DateTimeOffset now)
    {
        var payload = JsonSerializer.Serialize(new
        {
            bookingId = booking.Id,
            slotId = slot.Id,
            resourceId = slot.ResourceId,
            customerReference = booking.CustomerReference,
            status = booking.Status.ToString(),
            slotStartUtc = slot.StartUtc,
            slotEndUtc = slot.EndUtc,
            occurredAtUtc = now,
        });

        return new OutboxMessage(type, payload, now);
    }
}
