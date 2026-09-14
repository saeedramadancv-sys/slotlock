using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlotLock.Application.Abstractions;
using SlotLock.Application.Bookings;
using SlotLock.Application.Common;
using SlotLock.Application.Options;
using SlotLock.Domain.Common;
using SlotLock.Domain.Entities;

namespace SlotLock.Application.Scheduling;

/// <summary>
/// How a resource's slots should be laid out over a date range.
/// </summary>
/// <param name="DailyStart">Opening time, in the resource's own time zone.</param>
/// <param name="DailyEnd">
/// Closing time, in the resource's own time zone. A slot that would run past it is not
/// created: a 30-minute appointment starting at 16:45 against a 17:00 close is a booking the
/// resource cannot honour.
/// </param>
/// <param name="Days">
/// Which weekdays to generate. Null means every day. The default working week differs by
/// country - Sunday to Thursday in Jordan, Monday to Friday in much of Europe - so it is a
/// parameter rather than a built-in assumption.
/// </param>
public sealed record SlotPlan(
    DateOnly FromDate,
    DateOnly ToDate,
    TimeOnly DailyStart,
    TimeOnly DailyEnd,
    TimeSpan SlotDuration,
    int? Capacity = null,
    IReadOnlyCollection<DayOfWeek>? Days = null);

/// <summary>What a generation run did.</summary>
public sealed record SlotPlanResult(int Created, int SkippedExisting, int SkippedNonexistentLocalTime);

/// <summary>
/// Creates resources and lays their slots out over a calendar.
/// </summary>
public sealed class ScheduleService
{
    private readonly IResourceRepository _resources;
    private readonly ISlotRepository _slots;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly BookingOptions _options;
    private readonly ILogger<ScheduleService> _logger;

    public ScheduleService(
        IResourceRepository resources,
        ISlotRepository slots,
        IUnitOfWork unitOfWork,
        TimeProvider time,
        IOptions<BookingOptions> options,
        ILogger<ScheduleService> logger)
    {
        _resources = resources;
        _slots = slots;
        _unitOfWork = unitOfWork;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ResourceDto> CreateResourceAsync(
        string name,
        string timeZoneId,
        int defaultCapacity,
        CancellationToken cancellationToken = default)
    {
        var resource = new Resource(name, timeZoneId, _time.GetUtcNow(), defaultCapacity);
        _resources.Add(resource);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ResourceDto.From(resource);
    }

    public async Task<IReadOnlyList<ResourceDto>> ListResourcesAsync(CancellationToken cancellationToken = default)
    {
        var resources = await _resources.ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. resources.Select(ResourceDto.From)];
    }

    /// <summary>
    /// Generates the slots described by <paramref name="plan"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the local clock, not UTC arithmetic.</b> "Open 09:00 to 17:00" is a statement
    /// about the wall clock where the resource is. Generating in UTC and converting at the
    /// end works until a daylight-saving transition, after which every appointment sits an
    /// hour off and nobody notices for months. Each slot start is therefore built as a local
    /// time and converted individually.
    /// </para>
    /// <para>
    /// <b>Spring forward.</b> Clocks jumping from 02:00 to 03:00 mean local times in the gap
    /// never happen. Those slots are skipped and counted, rather than converted into whatever
    /// the runtime picks for a time that does not exist.
    /// </para>
    /// <para>
    /// <b>Fall back.</b> When an hour repeats, a local time maps to two instants. The later of
    /// the two is used - the standard-time occurrence - because that is what
    /// <see cref="TimeZoneInfo.GetUtcOffset(DateTime)"/> resolves an ambiguous local time to.
    /// The choice is pinned by a test rather than left to whatever the framework happens to
    /// do, since a silent one-hour shift on one day a year is the kind of bug that is found
    /// by a customer.
    /// </para>
    /// <para>
    /// Re-running over a range that already has slots adds nothing. Generation is something
    /// an operator repeats and a deployment script re-applies, so it has to be safe to
    /// repeat.
    /// </para>
    /// </remarks>
    public async Task<SlotPlanResult> GenerateSlotsAsync(
        Guid resourceId,
        SlotPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var resource = await _resources.FindAsync(resourceId, cancellationToken).ConfigureAwait(false)
            ?? throw new NotFoundException(nameof(Resource), resourceId);

        Validate(plan);

        var timeZone = resource.TimeZone();
        var capacity = plan.Capacity ?? resource.DefaultCapacity;
        var now = _time.GetUtcNow();

        // One query for the whole range, rather than an existence check per slot. Generating
        // a quarter of appointments is thousands of slots, and thousands of round trips is
        // how a harmless-looking admin action times out.
        var windowStartUtc = ToUtcOrNull(plan.FromDate.ToDateTime(TimeOnly.MinValue), timeZone)
            ?? new DateTimeOffset(plan.FromDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var windowEndUtc = ToUtcOrNull(plan.ToDate.AddDays(1).ToDateTime(TimeOnly.MinValue), timeZone)
            ?? new DateTimeOffset(plan.ToDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var existing = (await _slots
                .ListStartTimesAsync(resourceId, windowStartUtc, windowEndUtc, cancellationToken)
                .ConfigureAwait(false))
            .ToHashSet();

        var created = new List<Slot>();
        var skippedExisting = 0;
        var skippedNonexistent = 0;

        for (var date = plan.FromDate; date <= plan.ToDate; date = date.AddDays(1))
        {
            if (plan.Days is { Count: > 0 } && !plan.Days.Contains(date.DayOfWeek))
            {
                continue;
            }

            for (var localStart = plan.DailyStart;
                 localStart.Add(plan.SlotDuration) <= plan.DailyEnd;
                 localStart = localStart.Add(plan.SlotDuration))
            {
                var startUtc = ToUtcOrNull(date.ToDateTime(localStart), timeZone);
                if (startUtc is null)
                {
                    skippedNonexistent++;
                    continue;
                }

                if (!existing.Add(startUtc.Value))
                {
                    skippedExisting++;
                    continue;
                }

                created.Add(new Slot(
                    resourceId,
                    startUtc.Value,
                    startUtc.Value.Add(plan.SlotDuration),
                    capacity,
                    now));
            }
        }

        if (created.Count > 0)
        {
            _slots.AddRange(created);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Generated {Created} slot(s) for resource {ResourceId}; skipped {Existing} existing and {Nonexistent} across a clock change.",
            created.Count,
            resourceId,
            skippedExisting,
            skippedNonexistent);

        return new SlotPlanResult(created.Count, skippedExisting, skippedNonexistent);
    }

    /// <summary>
    /// Converts a local wall-clock time to the instant it refers to, or null when that local
    /// time does not exist because clocks moved forward over it.
    /// </summary>
    private static DateTimeOffset? ToUtcOrNull(DateTime localTime, TimeZoneInfo timeZone)
    {
        localTime = DateTime.SpecifyKind(localTime, DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(localTime))
        {
            return null;
        }

        // For an ambiguous local time this returns the standard-time offset, so the second of
        // the two occurrences is the one scheduled. Verified, not assumed: the opposite is
        // just as plausible to read, and the difference is an hour.
        var offset = timeZone.GetUtcOffset(localTime);
        return new DateTimeOffset(localTime, offset).ToUniversalTime();
    }

    private void Validate(SlotPlan plan)
    {
        if (plan.ToDate < plan.FromDate)
        {
            throw new DomainException("invalid_range", "The end date must not precede the start date.");
        }

        if (plan.SlotDuration <= TimeSpan.Zero)
        {
            throw new DomainException("invalid_duration", "Slot duration must be positive.");
        }

        if (plan.DailyEnd <= plan.DailyStart)
        {
            // Overnight windows would need a second date to be unambiguous; refused rather
            // than guessed at.
            throw new DomainException("invalid_daily_window", "The closing time must be after the opening time.");
        }

        if (plan.SlotDuration > plan.DailyEnd - plan.DailyStart)
        {
            throw new DomainException(
                "slot_longer_than_day",
                "A single slot cannot be longer than the resource's opening hours.");
        }

        var span = plan.ToDate.ToDateTime(TimeOnly.MinValue) - plan.FromDate.ToDateTime(TimeOnly.MinValue);
        if (span > _options.MaxAvailabilityWindow)
        {
            throw new DomainException(
                "range_too_wide",
                $"Generate at most {_options.MaxAvailabilityWindow.TotalDays:0} days at a time.");
        }
    }
}
