using Microsoft.AspNetCore.Mvc;
using SlotLock.Api.Contracts;
using SlotLock.Application.Bookings;
using SlotLock.Application.Scheduling;

namespace SlotLock.Api.Controllers;

/// <summary>Resources, their generated slots, and what is free.</summary>
[ApiController]
[Route("api/v1/resources")]
[Produces("application/json")]
public sealed class ResourcesController : ControllerBase
{
    private readonly ScheduleService _schedule;
    private readonly AvailabilityService _availability;

    public ResourcesController(ScheduleService schedule, AvailabilityService availability)
    {
        _schedule = schedule;
        _availability = availability;
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ResourceDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ResourceDto>>> List(CancellationToken cancellationToken) =>
        Ok(await _schedule.ListResourcesAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Creates a bookable resource.</summary>
    [HttpPost]
    [ProducesResponseType<ResourceDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ResourceDto>> Create(
        [FromBody] CreateResourceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resource = await _schedule
            .CreateResourceAsync(request.Name, request.TimeZoneId, request.DefaultCapacity, cancellationToken)
            .ConfigureAwait(false);

        return CreatedAtAction(nameof(List), new { id = resource.Id }, resource);
    }

    /// <summary>
    /// Lays slots out over a date range from the resource's local opening hours.
    /// </summary>
    /// <remarks>
    /// Safe to repeat: a second run over the same range creates nothing and reports what it
    /// skipped. Slot generation is something operators re-run and deployment scripts
    /// re-apply, so it has to be repeatable rather than merely idempotent-by-luck.
    /// </remarks>
    [HttpPost("{id:guid}/slots")]
    [ProducesResponseType<SlotPlanResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SlotPlanResult>> GenerateSlots(
        Guid id,
        [FromBody] GenerateSlotsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var plan = new SlotPlan(
            request.FromDate,
            request.ToDate,
            request.DailyStart,
            request.DailyEnd,
            TimeSpan.FromMinutes(request.SlotMinutes),
            request.Capacity,
            request.Days);

        return Ok(await _schedule.GenerateSlotsAsync(id, plan, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>What is free on this resource between two instants.</summary>
    /// <param name="onlyBookable">
    /// Defaults to true. Pass false to include full slots with a remaining count of zero,
    /// which a calendar view needs in order to draw a day as booked rather than as empty.
    /// </param>
    /// <remarks>
    /// The counts are a snapshot. A seat shown as free can be taken before the caller acts on
    /// it; that race is settled when the booking is attempted, not by this read.
    /// </remarks>
    [HttpGet("{id:guid}/availability")]
    [ProducesResponseType<IReadOnlyList<SlotAvailabilityDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IReadOnlyList<SlotAvailabilityDto>>> Availability(
        Guid id,
        [FromQuery] DateTimeOffset from,
        [FromQuery] DateTimeOffset to,
        [FromQuery] bool onlyBookable = true,
        CancellationToken cancellationToken = default) =>
        Ok(await _availability
            .GetAsync(id, from, to, onlyBookable, cancellationToken)
            .ConfigureAwait(false));
}
