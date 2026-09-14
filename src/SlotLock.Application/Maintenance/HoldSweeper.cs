using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlotLock.Application.Abstractions;
using SlotLock.Application.Common;
using SlotLock.Application.Options;
using SlotLock.Domain.Entities;

namespace SlotLock.Application.Maintenance;

/// <summary>
/// Reclaims seats from holds nobody confirmed.
/// </summary>
/// <remarks>
/// <para>
/// A hold takes a seat out of circulation the moment it is created. If the customer closes
/// the tab, nothing else will ever release it, and a busy resource slowly fills with seats
/// owed to people who left. This sweeper is what makes holds safe to use at all.
/// </para>
/// <para>
/// <b>It is not part of the correctness argument.</b> A lapsed hold can never be confirmed -
/// <see cref="Booking.Confirm"/> checks the deadline itself - so a sweeper that is late, or
/// down, delays capacity coming back but cannot let a seat be sold twice. That separation is
/// deliberate: correctness that depends on a background job running on time is correctness
/// that fails during an incident.
/// </para>
/// </remarks>
public sealed class HoldSweeper
{
    private readonly IBookingRepository _bookings;
    private readonly ISlotRepository _slots;
    private readonly IOutboxRepository _outbox;
    private readonly IIdempotencyStore _idempotency;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ConcurrencyRetryPolicy _retry;
    private readonly TimeProvider _time;
    private readonly BookingOptions _options;
    private readonly ILogger<HoldSweeper> _logger;

    public HoldSweeper(
        IBookingRepository bookings,
        ISlotRepository slots,
        IOutboxRepository outbox,
        IIdempotencyStore idempotency,
        IUnitOfWork unitOfWork,
        ConcurrencyRetryPolicy retry,
        TimeProvider time,
        IOptions<BookingOptions> options,
        ILogger<HoldSweeper> logger)
    {
        _bookings = bookings;
        _slots = slots;
        _outbox = outbox;
        _idempotency = idempotency;
        _unitOfWork = unitOfWork;
        _retry = retry;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Expires one batch of lapsed holds and returns how many seats were reclaimed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The batch is expired and saved as a unit rather than one booking at a time. Saving per
    /// booking would mean a round trip each, and a backlog of thousands after an outage would
    /// take minutes of chatter to clear.
    /// </para>
    /// <para>
    /// The sweeper competes for the same slot rows as live bookings, so a conflict here is
    /// expected rather than exceptional. On conflict the whole batch is re-read and replayed:
    /// bookings a customer confirmed in the meantime are no longer held, so
    /// <see cref="Booking.Expire"/> declines them and the pass converges instead of fighting.
    /// </para>
    /// </remarks>
    public Task<int> SweepAsync(CancellationToken cancellationToken = default) =>
        _retry.ExecuteAsync(
            async (attempt, ct) =>
            {
                if (attempt > 1)
                {
                    _unitOfWork.Reset();
                }

                var now = _time.GetUtcNow();
                var lapsed = await _bookings
                    .ListLapsedHoldsAsync(now, _options.SweepBatchSize, ct)
                    .ConfigureAwait(false);

                if (lapsed.Count == 0)
                {
                    return 0;
                }

                var reclaimed = 0;

                foreach (var booking in lapsed)
                {
                    if (!booking.Expire(now))
                    {
                        // Confirmed or cancelled between the query and now. Not an error:
                        // this is exactly the race the return value exists to report.
                        continue;
                    }

                    var slot = await _slots.FindAsync(booking.SlotId, ct).ConfigureAwait(false);
                    slot?.Release();
                    reclaimed++;

                    _outbox.Add(new OutboxMessage(
                        "booking.expired",
                        JsonSerializer.Serialize(new
                        {
                            bookingId = booking.Id,
                            slotId = booking.SlotId,
                            customerReference = booking.CustomerReference,
                            occurredAtUtc = now,
                        }),
                        now));
                }

                await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

                if (reclaimed > 0)
                {
                    _logger.LogInformation(
                        "Reclaimed {Reclaimed} seat(s) from lapsed holds out of {Examined} examined.",
                        reclaimed,
                        lapsed.Count);
                }

                return reclaimed;
            },
            cancellationToken);

    /// <summary>
    /// Deletes idempotency records past their retention window.
    /// </summary>
    /// <remarks>
    /// Housekeeping rather than correctness, but it is the difference between a table that
    /// stays small and one that grows by every write request forever.
    /// </remarks>
    public async Task<int> PurgeIdempotencyAsync(CancellationToken cancellationToken = default)
    {
        var purged = await _idempotency
            .PurgeExpiredAsync(_time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        if (purged > 0)
        {
            _logger.LogInformation("Purged {Purged} expired idempotency record(s).", purged);
        }

        return purged;
    }
}
