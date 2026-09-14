using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SlotLock.Application.Abstractions;
using SlotLock.Application.Bookings;
using SlotLock.Application.Maintenance;
using SlotLock.Domain.Entities;
using SlotLock.Domain.Enums;
using SlotLock.IntegrationTests.Infrastructure;

namespace SlotLock.IntegrationTests;

/// <summary>
/// The outbox: a side effect that survives whatever happens to the process, and never fires
/// for work that rolled back.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OutboxTests : IAsyncLifetime
{
    private readonly DatabaseFixture _fixture;

    public OutboxTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Booking_and_its_message_are_written_by_the_same_transaction()
    {
        var slot = await Seed.SlotAsync(_fixture, capacity: 1);

        await _fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/bookings",
            new { slotId = slot, customerReference = "customer@example.com" });

        await using var context = _fixture.Factory.CreateDbContext();

        var message = await context.OutboxMessages.AsNoTracking().SingleAsync();

        message.Type.Should().Be("booking.held");
        message.Status.Should().Be(OutboxStatus.Pending);

        // The payload carries the booking id, which is what an at-least-once consumer needs
        // in order to recognise a repeat delivery.
        var booking = await context.Bookings.AsNoTracking().SingleAsync();
        message.Payload.Should().Contain(booking.Id.ToString());
    }

    [Fact]
    public async Task No_message_is_written_when_the_booking_is_refused()
    {
        // The guarantee that makes the pattern worth its cost: nobody is emailed about a seat
        // they did not get. A pre-commit send would have fired here.
        var slot = await Seed.SlotAsync(_fixture, capacity: 1);
        var client = _fixture.Factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/bookings", new { slotId = slot, customerReference = "first@example.com" });
        await client.PostAsJsonAsync("/api/v1/bookings", new { slotId = slot, customerReference = "second@example.com" });

        await using var context = _fixture.Factory.CreateDbContext();

        (await context.OutboxMessages.CountAsync()).Should().Be(1, "only the successful booking produced an event");
    }

    [Fact]
    public async Task A_dispatched_message_is_marked_processed_and_not_sent_again()
    {
        var slot = await Seed.SlotAsync(_fixture, capacity: 1);
        await _fixture.Factory.CreateClient().PostAsJsonAsync(
            "/api/v1/bookings",
            new { slotId = slot, customerReference = "customer@example.com" });

        var handler = new CountingHandler();

        (await RunDispatcher(handler)).Dispatched.Should().Be(1);
        handler.Calls.Should().Be(1);

        // A second pass must find nothing. If processed rows stayed eligible, every customer
        // would be emailed once per poll interval, forever.
        (await RunDispatcher(handler)).Dispatched.Should().Be(0);
        handler.Calls.Should().Be(1);

        await using var context = _fixture.Factory.CreateDbContext();
        var message = await context.OutboxMessages.AsNoTracking().SingleAsync();

        message.Status.Should().Be(OutboxStatus.Processed);
        message.ProcessedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task A_failing_message_is_retried_later_rather_than_immediately()
    {
        await SeedMessage();

        var handler = new CountingHandler { ThrowWith = "smtp unavailable" };

        var result = await RunDispatcher(handler);

        result.Failed.Should().Be(1);
        result.DeadLettered.Should().Be(0);

        await using var context = _fixture.Factory.CreateDbContext();
        var message = await context.OutboxMessages.AsNoTracking().SingleAsync();

        message.Status.Should().Be(OutboxStatus.Pending, "one failure is not a permanent one");
        message.Attempts.Should().Be(1);
        message.LastError.Should().Contain("smtp unavailable");

        // Scheduled forward, so the next pass does not hammer a service that is already down.
        message.NextAttemptAtUtc.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_dead_lettered_rather_than_retried_forever()
    {
        await SeedMessage();

        var handler = new CountingHandler { ThrowWith = "permanently broken" };

        // Each pass moves the schedule forward, so the row has to be made due again to
        // simulate time passing rather than the suite actually waiting half an hour.
        for (var i = 0; i < OutboxMessage.MaxAttempts; i++)
        {
            await MakeDueNow();
            await RunDispatcher(handler);
        }

        await using var context = _fixture.Factory.CreateDbContext();
        var message = await context.OutboxMessages.AsNoTracking().SingleAsync();

        message.Status.Should().Be(OutboxStatus.Dead);
        message.Attempts.Should().Be(OutboxMessage.MaxAttempts);

        // Kept, not deleted: a dead-lettered message is evidence, and someone has to be able
        // to look at it and replay it once the cause is fixed.
        (await context.OutboxMessages.CountAsync()).Should().Be(1);

        await MakeDueNow();
        (await RunDispatcher(handler)).Total.Should().Be(0, "a dead message is never picked up again");
    }

    [Fact]
    public async Task One_poisoned_message_does_not_block_the_ones_behind_it()
    {
        // Head-of-line blocking is how a single malformed payload silently stops everybody's
        // email. The dispatcher must fail that one message and carry on.
        await SeedMessage("booking.poisoned");
        await SeedMessage("booking.fine");

        var handler = new SelectiveHandler(failWhenTypeContains: "poisoned");

        var result = await RunDispatcher(handler);

        result.Dispatched.Should().Be(1);
        result.Failed.Should().Be(1);

        await using var context = _fixture.Factory.CreateDbContext();

        (await context.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == "booking.fine"))
            .Status.Should().Be(OutboxStatus.Processed);
    }

    private async Task SeedMessage(string type = "booking.confirmed")
    {
        await using var context = _fixture.Factory.CreateDbContext();
        context.OutboxMessages.Add(new OutboxMessage(type, """{"bookingId":"seed"}""", DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();
    }

    /// <summary>Pulls every pending message's next attempt back to now.</summary>
    private async Task MakeDueNow()
    {
        await using var context = _fixture.Factory.CreateDbContext();
        await context.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAtUtc, DateTimeOffset.UtcNow.AddSeconds(-1)));
    }

    private async Task<OutboxRunResult> RunDispatcher(IOutboxMessageHandler handler)
    {
        await using var scope = _fixture.Factory.CreateScope();

        // Built by hand rather than resolved, so the test supplies the handler. The
        // dispatcher's own collaborators still come from the container, so the wiring under
        // test is the real wiring.
        var dispatcher = ActivatorUtilities.CreateInstance<OutboxDispatcher>(scope.ServiceProvider, handler);

        return await dispatcher.RunOnceAsync();
    }

    private sealed class CountingHandler : IOutboxMessageHandler
    {
        public int Calls { get; private set; }

        public string? ThrowWith { get; init; }

        public Task HandleAsync(Guid messageId, string type, string payload, CancellationToken cancellationToken = default)
        {
            Calls++;

            return ThrowWith is null
                ? Task.CompletedTask
                : throw new InvalidOperationException(ThrowWith);
        }
    }

    private sealed class SelectiveHandler : IOutboxMessageHandler
    {
        private readonly string _failWhenTypeContains;

        public SelectiveHandler(string failWhenTypeContains) => _failWhenTypeContains = failWhenTypeContains;

        public Task HandleAsync(Guid messageId, string type, string payload, CancellationToken cancellationToken = default) =>
            type.Contains(_failWhenTypeContains, StringComparison.Ordinal)
                ? throw new InvalidOperationException("cannot handle this one")
                : Task.CompletedTask;
    }
}
