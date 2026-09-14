using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SlotLock.Application.Bookings;
using SlotLock.Domain.Enums;
using SlotLock.IntegrationTests.Infrastructure;

namespace SlotLock.IntegrationTests;

/// <summary>
/// The tests this service exists to pass: many callers, one seat, exactly one winner.
/// </summary>
/// <remarks>
/// <para>
/// These run against real SQL Server over real HTTP. Each request gets its own connection and
/// its own transaction, so the interleaving is the database's, not a simulation of one. Remove
/// the <c>rowversion</c> mapping from <c>SlotConfiguration</c> and these fail - which is the
/// only way to know the mapping is doing anything.
/// </para>
/// <para>
/// The counts are asserted two ways: what the API told each caller, and what the database
/// holds afterwards. A service can answer 201 to one caller and still have written two rows.
/// </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class OverbookingTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;

    public OverbookingTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task One_seat_and_fifty_simultaneous_callers_produces_exactly_one_booking()
    {
        const int callers = 50;

        var slot = await Seed.SlotAsync(_fixture, capacity: 1);

        var responses = await FireSimultaneously(callers, slot);

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var conflicted = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        created.Should().Be(1, "a single seat can be sold exactly once");
        conflicted.Should().Be(callers - 1, "everyone else must be told no, not given a seat");

        // The API's answers and the database must agree. Asserting only the responses would
        // pass even if every caller had written a row and the service simply lied to 49 of
        // them.
        await using var context = _fixture.Factory.CreateDbContext();

        var stored = await context.Slots.AsNoTracking().FirstAsync(s => s.Id == slot);
        stored.ReservedCount.Should().Be(1);
        stored.ReservedCount.Should().BeLessThanOrEqualTo(stored.Capacity);

        var live = await context.Bookings
            .AsNoTracking()
            .CountAsync(b => b.SlotId == slot && (b.Status == BookingStatus.Held || b.Status == BookingStatus.Confirmed));

        live.Should().Be(1);
    }

    [Theory]
    [InlineData(5, 40)]
    [InlineData(12, 60)]
    public async Task Capacity_is_the_exact_ceiling_however_many_callers_arrive(int capacity, int callers)
    {
        var slot = await Seed.SlotAsync(_fixture, capacity);

        var responses = await FireSimultaneously(callers, slot);

        responses.Count(r => r.StatusCode == HttpStatusCode.Created)
            .Should().Be(capacity, "every seat should be sold - refusing a free seat is as wrong as overselling");

        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict)
            .Should().Be(callers - capacity);

        await using var context = _fixture.Factory.CreateDbContext();
        var stored = await context.Slots.AsNoTracking().FirstAsync(s => s.Id == slot);
        stored.ReservedCount.Should().Be(capacity);
    }

    [Fact]
    public async Task Cancelling_under_load_returns_the_seat_without_double_releasing_it()
    {
        // The mirror image of overselling. If a cancellation can release a seat twice, the
        // slot ends up with more capacity than it has, and the next round oversells - a bug
        // that only shows up one step removed from its cause.
        var slot = await Seed.SlotAsync(_fixture, capacity: 1);

        var client = _fixture.Factory.CreateClient();
        var held = await client.PostAsJsonAsync(
            "/api/v1/bookings",
            new { slotId = slot, customerReference = "first@example.com" });

        held.StatusCode.Should().Be(HttpStatusCode.Created);
        var booking = await held.Content.ReadFromJsonAsync<BookingDto>();

        // Ten simultaneous cancellations of the same booking.
        var cancels = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
            _fixture.Factory.CreateClient().DeleteAsync($"/api/v1/bookings/{booking!.Id}")));

        cancels.Should().OnlyContain(r => r.IsSuccessStatusCode, "cancelling an already-cancelled booking is not an error");

        await using var context = _fixture.Factory.CreateDbContext();
        var stored = await context.Slots.AsNoTracking().FirstAsync(s => s.Id == slot);

        stored.ReservedCount.Should().Be(0, "the seat comes back exactly once, however many times it is cancelled");
    }

    [Fact]
    public async Task A_freed_seat_can_be_taken_again()
    {
        var slot = await Seed.SlotAsync(_fixture, capacity: 1);
        var client = _fixture.Factory.CreateClient();

        var first = await client.PostAsJsonAsync(
            "/api/v1/bookings",
            new { slotId = slot, customerReference = "first@example.com" });
        var booking = await first.Content.ReadFromJsonAsync<BookingDto>();

        var blocked = await client.PostAsJsonAsync(
            "/api/v1/bookings",
            new { slotId = slot, customerReference = "second@example.com" });
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await client.DeleteAsync($"/api/v1/bookings/{booking!.Id}");

        var retry = await client.PostAsJsonAsync(
            "/api/v1/bookings",
            new { slotId = slot, customerReference = "second@example.com" });

        retry.StatusCode.Should().Be(HttpStatusCode.Created, "releasing a seat must actually put it back on sale");
    }

    /// <summary>
    /// Releases every request at the same instant.
    /// </summary>
    /// <remarks>
    /// Starting tasks in a loop is not enough: the first is often finished before the last is
    /// scheduled, and the test then passes without any concurrency having occurred. The
    /// barrier makes every caller wait and then go together, so the race is real.
    /// </remarks>
    private async Task<HttpResponseMessage[]> FireSimultaneously(int callers, Guid slotId)
    {
        using var gate = new SemaphoreSlim(0, callers);

        var calls = Enumerable.Range(0, callers).Select(async i =>
        {
            var client = _fixture.Factory.CreateClient();
            await gate.WaitAsync();

            return await client.PostAsJsonAsync(
                "/api/v1/bookings",
                new { slotId, customerReference = $"customer-{i}@example.com" });
        }).ToArray();

        gate.Release(callers);
        return await Task.WhenAll(calls);
    }
}
