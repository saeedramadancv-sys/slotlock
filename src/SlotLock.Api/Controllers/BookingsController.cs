using Microsoft.AspNetCore.Mvc;
using SlotLock.Api.Contracts;
using SlotLock.Api.Idempotency;
using SlotLock.Application.Bookings;

namespace SlotLock.Api.Controllers;

/// <summary>
/// Holding, confirming and cancelling seats.
/// </summary>
/// <remarks>
/// The two-step hold-then-confirm flow is the API's shape for a reason. A single "book"
/// call forces the server to decide the outcome before the customer has paid: either the
/// seat is given away and may never be paid for, or it is withheld and someone else takes it
/// mid-checkout. The hold makes that window explicit and bounded.
/// </remarks>
[ApiController]
[Route("api/v1/bookings")]
[Produces("application/json")]
public sealed class BookingsController : ControllerBase
{
    private readonly BookingService _bookings;

    public BookingsController(BookingService bookings) => _bookings = bookings;

    /// <summary>Reserves a seat for a bounded window.</summary>
    /// <response code="201">The seat is held. Confirm it before the hold expires.</response>
    /// <response code="409">The slot is full, or too contended to settle.</response>
    /// <response code="422">The slot has already started, or the resource is not taking bookings.</response>
    [HttpPost]
    [Idempotent]
    [ProducesResponseType<BookingDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BookingDto>> Hold(
        [FromBody] HoldBookingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var booking = await _bookings
            .HoldAsync(request.SlotId, request.CustomerReference, cancellationToken)
            .ConfigureAwait(false);

        return CreatedAtAction(nameof(Get), new { id = booking.Id }, booking);
    }

    /// <summary>Makes a held seat final.</summary>
    /// <response code="200">Confirmed. Repeating the call is safe and returns the same booking.</response>
    /// <response code="409">The hold expired, or the booking was already cancelled.</response>
    [HttpPost("{id:guid}/confirm")]
    [ProducesResponseType<BookingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BookingDto>> Confirm(Guid id, CancellationToken cancellationToken) =>
        Ok(await _bookings.ConfirmAsync(id, cancellationToken).ConfigureAwait(false));

    /// <summary>Releases a seat, whether it was held or confirmed.</summary>
    /// <response code="200">
    /// Cancelled. Cancelling an already-cancelled booking is not an error: the caller asked
    /// for a state, and it is in that state.
    /// </response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType<BookingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDto>> Cancel(Guid id, CancellationToken cancellationToken) =>
        Ok(await _bookings.CancelAsync(id, cancellationToken).ConfigureAwait(false));

    /// <summary>Reads one booking.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<BookingDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BookingDto>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(await _bookings.GetAsync(id, cancellationToken).ConfigureAwait(false));
}
