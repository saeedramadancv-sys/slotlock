using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using SlotLock.Application.Abstractions;
using SlotLock.Application.Options;
using SlotLock.Application.Scheduling;
using SlotLock.Domain.Common;
using SlotLock.Domain.Entities;

namespace SlotLock.UnitTests.Application;

/// <summary>
/// Slot generation, and what happens to a working day when the clocks change.
/// </summary>
/// <remarks>
/// <para>
/// Opening hours are a statement about the wall clock where the resource is. Twice a year
/// that clock is not a continuous line: an hour disappears in spring and repeats in autumn.
/// A generator that does UTC arithmetic and converts at the end gets both wrong, and the
/// symptom is every appointment sitting an hour off for six months.
/// </para>
/// <para>
/// The repositories are substitutes: this is arithmetic over a calendar, and involving a
/// database would slow it down without testing anything more.
/// </para>
/// </remarks>
public class ScheduleServiceTests
{
    private const string London = "Europe/London";

    private readonly ISlotRepository _slots = Substitute.For<ISlotRepository>();
    private readonly IResourceRepository _resources = Substitute.For<IResourceRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly List<Slot> _created = [];

    public ScheduleServiceTests()
    {
        _slots.ListStartTimesAsync(default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<DateTimeOffset>>([]));

        _slots.When(s => s.AddRange(Arg.Any<IEnumerable<Slot>>()))
            .Do(call => _created.AddRange(call.Arg<IEnumerable<Slot>>()));
    }

    [Fact]
    public async Task A_plain_day_generates_one_slot_per_interval()
    {
        var service = Service(out var resource);

        var result = await service.GenerateSlotsAsync(
            resource.Id,
            new SlotPlan(
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 1),
                new TimeOnly(9, 0),
                new TimeOnly(12, 0),
                TimeSpan.FromMinutes(30)));

        result.Created.Should().Be(6);
        _created.Should().HaveCount(6);
        _created.Should().BeInAscendingOrder(s => s.StartUtc);
    }

    [Fact]
    public async Task A_slot_that_would_overrun_closing_time_is_not_created()
    {
        // A 45-minute appointment starting at 16:30 against a 17:00 close is a booking the
        // resource cannot honour.
        var service = Service(out var resource);

        var result = await service.GenerateSlotsAsync(
            resource.Id,
            new SlotPlan(
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 1),
                new TimeOnly(16, 0),
                new TimeOnly(17, 0),
                TimeSpan.FromMinutes(45)));

        result.Created.Should().Be(1);
        _created.Single().EndUtc.Should().Be(_created.Single().StartUtc.AddMinutes(45));
    }

    [Fact]
    public async Task Clocks_going_forward_skip_the_hour_that_does_not_exist()
    {
        // 29 March 2026, London: 01:00 becomes 02:00. Local times from 01:00 to 01:59 never
        // happen, so the two half-hour slots in that gap cannot be created. Converting them
        // anyway would put appointments at instants nobody can arrive at.
        var service = Service(out var resource, London);

        var result = await service.GenerateSlotsAsync(
            resource.Id,
            new SlotPlan(
                new DateOnly(2026, 3, 29),
                new DateOnly(2026, 3, 29),
                new TimeOnly(0, 0),
                new TimeOnly(4, 0),
                TimeSpan.FromMinutes(30)));

        result.SkippedNonexistentLocalTime.Should().Be(2);
        result.Created.Should().Be(6, "a four-hour window minus the hour that vanished");

        _created.Select(s => s.StartUtc).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Clocks_going_back_produce_one_slot_per_local_time_not_two()
    {
        // 25 October 2026, London: 02:00 becomes 01:00, so 01:00-01:59 happens twice. Each
        // local start time must still yield exactly one slot - otherwise the schedule shows a
        // duplicated hour once a year and the duplicates compete for the same staff.
        var service = Service(out var resource, London);

        var result = await service.GenerateSlotsAsync(
            resource.Id,
            new SlotPlan(
                new DateOnly(2026, 10, 25),
                new DateOnly(2026, 10, 25),
                new TimeOnly(0, 0),
                new TimeOnly(4, 0),
                TimeSpan.FromMinutes(30)));

        result.Created.Should().Be(8);
        result.SkippedNonexistentLocalTime.Should().Be(0);
        _created.Select(s => s.StartUtc).Should().OnlyHaveUniqueItems();

        // Which of the two occurrences is chosen, pinned rather than assumed. 01:30 local is
        // ambiguous: 00:30 UTC under BST, or 01:30 UTC under GMT. TimeZoneInfo.GetUtcOffset
        // resolves an ambiguous local time to the STANDARD offset, so the later of the two
        // instants is used. Asserted here because the behaviour is easy to state backwards,
        // and a one-hour error twice a year is exactly the kind of bug nobody notices.
        var ambiguous = _created.Should().ContainSingle(slot =>
            slot.StartUtc == new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero)).Subject;

        ambiguous.StartUtc.Should().Be(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Re_running_over_the_same_range_creates_nothing()
    {
        // Operators re-run generation and deployment scripts re-apply it. Doubling every
        // appointment the second time would be a data-loss-grade bug with no error message.
        var service = Service(out var resource);
        var existing = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

        _slots.ListStartTimesAsync(default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<DateTimeOffset>>([existing]));

        var result = await service.GenerateSlotsAsync(
            resource.Id,
            new SlotPlan(
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 1),
                new TimeOnly(9, 0),
                new TimeOnly(10, 0),
                TimeSpan.FromMinutes(30)));

        // 09:00 London in June is 08:00 UTC, which is the slot that already exists.
        result.SkippedExisting.Should().Be(1);
        result.Created.Should().Be(1);
    }

    [Fact]
    public async Task Only_the_requested_weekdays_are_generated()
    {
        // The working week is not universal - Sunday to Thursday in Jordan, Monday to Friday
        // in much of Europe - so it is asked for rather than assumed.
        var service = Service(out var resource);

        var result = await service.GenerateSlotsAsync(
            resource.Id,
            new SlotPlan(
                new DateOnly(2026, 6, 1),   // Monday
                new DateOnly(2026, 6, 7),   // Sunday
                new TimeOnly(9, 0),
                new TimeOnly(10, 0),
                TimeSpan.FromMinutes(60),
                Days: [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday]));

        result.Created.Should().Be(5, "Friday and Saturday are excluded");
    }

    [Fact]
    public async Task An_overnight_window_is_refused_rather_than_guessed_at()
    {
        var service = Service(out var resource);

        var act = async () => await service.GenerateSlotsAsync(
            resource.Id,
            new SlotPlan(
                new DateOnly(2026, 6, 1),
                new DateOnly(2026, 6, 1),
                new TimeOnly(22, 0),
                new TimeOnly(2, 0),
                TimeSpan.FromMinutes(30)));

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be("invalid_daily_window");
    }

    [Fact]
    public async Task A_range_wider_than_the_configured_ceiling_is_refused()
    {
        // Without a ceiling one request can ask for a decade and become a write of millions
        // of rows.
        var service = Service(out var resource);

        var act = async () => await service.GenerateSlotsAsync(
            resource.Id,
            new SlotPlan(
                new DateOnly(2026, 1, 1),
                new DateOnly(2027, 1, 1),
                new TimeOnly(9, 0),
                new TimeOnly(17, 0),
                TimeSpan.FromMinutes(30)));

        (await act.Should().ThrowAsync<DomainException>()).Which.Code.Should().Be("range_too_wide");
    }

    private ScheduleService Service(out Resource resource, string timeZoneId = London)
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        resource = new Resource("Clinic", timeZoneId, now, defaultCapacity: 1);

        _resources.FindAsync(resource.Id).ReturnsForAnyArgs(Task.FromResult<Resource?>(resource));

        return new ScheduleService(
            _resources,
            _slots,
            _unitOfWork,
            new FakeTimeProvider(now),
            Options.Create(new BookingOptions()),
            NullLogger<ScheduleService>.Instance);
    }
}
