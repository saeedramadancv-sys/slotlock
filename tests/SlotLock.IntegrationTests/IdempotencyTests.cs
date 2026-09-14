using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SlotLock.Application.Bookings;
using SlotLock.IntegrationTests.Infrastructure;

namespace SlotLock.IntegrationTests;

/// <summary>
/// Retrying a booking must not produce two bookings.
/// </summary>
/// <remarks>
/// The scenario is mundane and constant: a phone on a weak connection sends the request, the
/// response never arrives, the client retries. Without a key the customer ends up with two
/// appointments and one of them is never used.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class IdempotencyTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;

    public IdempotencyTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_same_key_replayed_returns_the_first_booking_rather_than_making_another()
    {
        var slot = await Seed.SlotAsync(_fixture, capacity: 5);
        var key = Guid.NewGuid().ToString();

        var first = await Post(slot, "customer@example.com", key);
        var second = await Post(slot, "customer@example.com", key);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created, "a replay returns the original outcome, including its status");

        var firstBooking = await first.Content.ReadFromJsonAsync<BookingDto>(TestJson.Options);
        var secondBooking = await second.Content.ReadFromJsonAsync<BookingDto>(TestJson.Options);

        secondBooking!.Id.Should().Be(firstBooking!.Id);
        second.Headers.Contains("Idempotency-Replayed").Should().BeTrue("a client should be able to tell a replay from fresh work");

        await using var context = _fixture.Factory.CreateDbContext();

        (await context.Bookings.CountAsync()).Should().Be(1);

        // The seat count is the part that would hurt. Two bookings on a five-seat slot is a
        // wasted seat; on a one-seat slot it is an oversell.
        var stored = await context.Slots.AsNoTracking().FirstAsync(s => s.Id == slot);
        stored.ReservedCount.Should().Be(1);
    }

    [Fact]
    public async Task Twenty_simultaneous_retries_of_one_request_still_make_one_booking()
    {
        // The realistic shape of a retry storm: a client, a proxy and a load balancer all
        // deciding the request timed out at the same moment.
        var slot = await Seed.SlotAsync(_fixture, capacity: 10);
        var key = Guid.NewGuid().ToString();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => Post(slot, "customer@example.com", key)));

        // Some racers arrive while the original is still running; they are told so rather
        // than being allowed to book. What must never happen is two seats for one key.
        responses.Should().OnlyContain(r =>
            r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);

        await using var context = _fixture.Factory.CreateDbContext();

        (await context.Bookings.CountAsync()).Should().Be(1);
        (await context.Slots.AsNoTracking().FirstAsync(s => s.Id == slot)).ReservedCount.Should().Be(1);
    }

    [Fact]
    public async Task The_same_key_with_a_different_body_is_refused()
    {
        // A client bug. Replaying the first response would hide it and hand back a booking
        // for someone else entirely.
        var slot = await Seed.SlotAsync(_fixture, capacity: 5);
        var key = Guid.NewGuid().ToString();

        await Post(slot, "first@example.com", key);
        var reused = await Post(slot, "second@example.com", key);

        reused.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var problem = await reused.Content.ReadFromJsonAsync<ProblemResponse>(TestJson.Options);
        problem!.Code.Should().Be("idempotency_key_reuse");
    }

    [Fact]
    public async Task A_failed_request_does_not_burn_its_key()
    {
        // The first attempt fails because the slot is full. If the failure were stored, the
        // client would keep being handed that 409 even after a seat came free - their retry,
        // the correct move, would be permanently useless.
        var slot = await Seed.SlotAsync(_fixture, capacity: 1);
        var client = _fixture.Factory.CreateClient();

        var taken = await Post(slot, "first@example.com", Guid.NewGuid().ToString());
        var firstBooking = await taken.Content.ReadFromJsonAsync<BookingDto>(TestJson.Options);

        var key = Guid.NewGuid().ToString();
        var refused = await Post(slot, "second@example.com", key);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        await client.DeleteAsync($"/api/v1/bookings/{firstBooking!.Id}");

        var retried = await Post(slot, "second@example.com", key);
        retried.StatusCode.Should().Be(HttpStatusCode.Created, "the key was never spent on a successful outcome");
    }

    [Fact]
    public async Task Without_a_key_two_identical_requests_make_two_bookings()
    {
        // Documents the boundary. The guarantee is opt-in; a caller that does not send a key
        // gets ordinary behaviour, and the API does not silently deduplicate on their behalf.
        var slot = await Seed.SlotAsync(_fixture, capacity: 5);

        await Post(slot, "customer@example.com", key: null);
        await Post(slot, "customer@example.com", key: null);

        await using var context = _fixture.Factory.CreateDbContext();
        (await context.Bookings.CountAsync()).Should().Be(2);
    }

    private Task<HttpResponseMessage> Post(Guid slotId, string customer, string? key)
    {
        var client = _fixture.Factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/bookings")
        {
            Content = JsonContent.Create(new { slotId, customerReference = customer }),
        };

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return client.SendAsync(request);
    }

    /// <summary>The parts of an RFC 9457 problem response these tests assert on.</summary>
    private sealed record ProblemResponse(string Title, int Status, string Code);
}
