using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SlotLock.Application.Bookings;
using SlotLock.Application.Maintenance;
using SlotLock.Domain.Entities;
using SlotLock.Domain.Enums;
using SlotLock.IntegrationTests.Infrastructure;

namespace SlotLock.IntegrationTests;

/// <summary>
/// Holds that nobody confirms have to come back, and holds that lapsed must never be
/// confirmable.
/// </summary>
/// <remarks>
/// Lapsed holds are seeded with an expiry already in the past rather than created and waited
/// out. Waiting would make the suite slow and, worse, timing-dependent - the kind of test
/// that fails once a fortnight on a loaded machine and gets rerun until it passes.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class HoldExpiryTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;

    public HoldExpiryTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_lapsed_hold_cannot_be_confirmed_even_before_the_sweeper_runs()
    {
        // The property that keeps correctness independent of a background job. If confirming
        // depended on the sweeper having run, the answer would change with how loaded the
        // server was, and a seat could be sold twice during an incident.
        var (slot, booking) = await SeedLapsedHold();

        var response = await _fixture.Factory
            .CreateClient()
            .PostAsync($"/api/v1/bookings/{booking}/confirm", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
        problem!.Code.Should().Be("hold_expired");

        // Still held and still counted: the seat is not free until the sweeper says so.
        await using var context = _fixture.Factory.CreateDbContext();
        (await context.Slots.AsNoTracking().FirstAsync(s => s.Id == slot)).ReservedCount.Should().Be(1);
    }

    [Fact]
    public async Task The_sweeper_reclaims_the_seat_and_marks_the_booking_expired()
    {
        var (slot, booking) = await SeedLapsedHold();

        var reclaimed = await RunSweeper();

        reclaimed.Should().Be(1);

        await using var context = _fixture.Factory.CreateDbContext();

        (await context.Slots.AsNoTracking().FirstAsync(s => s.Id == slot))
            .ReservedCount.Should().Be(0);

        (await context.Bookings.AsNoTracking().FirstAsync(b => b.Id == booking))
            .Status.Should().Be(BookingStatus.Expired);
    }

    [Fact]
    public async Task The_sweeper_leaves_a_confirmed_booking_alone()
    {
        // The sweeper queries, then acts. Between those two moments a customer can confirm,
        // and expiring them would cancel a seat somebody just paid for.
        var slot = await Seed.SlotAsync(_fixture, capacity: 1);
        var client = _fixture.Factory.CreateClient();

        var held = await client.PostAsJsonAsync(
            "/api/v1/bookings",
            new { slotId = slot, customerReference = "customer@example.com" });
        var booking = await held.Content.ReadFromJsonAsync<BookingDto>();

        await client.PostAsync($"/api/v1/bookings/{booking!.Id}/confirm", content: null);

        var reclaimed = await RunSweeper();

        reclaimed.Should().Be(0);

        await using var context = _fixture.Factory.CreateDbContext();

        (await context.Bookings.AsNoTracking().FirstAsync(b => b.Id == booking.Id))
            .Status.Should().Be(BookingStatus.Confirmed);

        (await context.Slots.AsNoTracking().FirstAsync(s => s.Id == slot))
            .ReservedCount.Should().Be(1, "a confirmed seat stays sold");
    }

    [Fact]
    public async Task Running_the_sweeper_twice_reclaims_each_seat_once()
    {
        // Sweeps overlap in practice - a slow pass, a restart, two instances. Reclaiming the
        // same seat twice would push the reserved count below what is actually booked, and
        // the slot would then oversell.
        await SeedLapsedHold();

        (await RunSweeper()).Should().Be(1);
        (await RunSweeper()).Should().Be(0);

        await using var context = _fixture.Factory.CreateDbContext();
        var slot = await context.Slots.AsNoTracking().FirstAsync();

        slot.ReservedCount.Should().Be(0);
        slot.ReservedCount.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task The_seat_a_sweeper_reclaims_can_be_sold_again()
    {
        var (slot, _) = await SeedLapsedHold();

        var blocked = await _fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/bookings",
            new { slotId = slot, customerReference = "second@example.com" });
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict, "the lapsed hold still occupies the seat");

        await RunSweeper();

        var retry = await _fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/bookings",
            new { slotId = slot, customerReference = "second@example.com" });

        retry.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>
    /// Writes a slot with one seat taken by a hold that expired ten minutes ago.
    /// </summary>
    private async Task<(Guid SlotId, Guid BookingId)> SeedLapsedHold()
    {
        var now = DateTimeOffset.UtcNow;
        var past = now.AddMinutes(-20);

        await using var context = _fixture.Factory.CreateDbContext();

        var resource = new Resource("Test resource", "Asia/Amman", now);
        var slot = new Slot(resource.Id, now.AddHours(1), now.AddHours(1).AddMinutes(30), 1, now);
        slot.Reserve();

        // Created twenty minutes ago with a ten-minute hold, so it lapsed ten minutes ago.
        var booking = Booking.Hold(slot.Id, "customer@example.com", past, TimeSpan.FromMinutes(10));

        context.Resources.Add(resource);
        context.Slots.Add(slot);
        context.Bookings.Add(booking);
        await context.SaveChangesAsync();

        return (slot.Id, booking.Id);
    }

    /// <summary>
    /// Runs one sweep directly, rather than waiting for the hosted worker.
    /// </summary>
    /// <remarks>
    /// The workers are disabled in the test host. Driving the component asserts what it does;
    /// waiting on a timer asserts that a timer fired, and takes thirty seconds to do it.
    /// </remarks>
    private async Task<int> RunSweeper()
    {
        await using var scope = _fixture.Factory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<HoldSweeper>().SweepAsync();
    }

    private sealed record ProblemResponse(string Title, int Status, string Code);
}
