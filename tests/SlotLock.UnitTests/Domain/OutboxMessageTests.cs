using FluentAssertions;
using SlotLock.Domain.Entities;
using SlotLock.Domain.Enums;

namespace SlotLock.UnitTests.Domain;

/// <summary>
/// Retry scheduling. Jitter is a parameter rather than a call to Random inside the entity,
/// which is what makes a backoff schedule assertable at all.
/// </summary>
public class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static OutboxMessage NewMessage() =>
        new("booking.confirmed", """{"bookingId":"x"}""", Now);

    [Fact]
    public void A_new_message_is_pending_and_due_immediately()
    {
        var message = NewMessage();

        message.Status.Should().Be(OutboxStatus.Pending);
        message.Attempts.Should().Be(0);
        message.NextAttemptAtUtc.Should().Be(Now);
    }

    [Fact]
    public void MarkProcessed_clears_the_last_error()
    {
        var message = NewMessage();
        message.MarkFailed(Now, "smtp timeout");

        message.MarkProcessed(Now.AddMinutes(1));

        message.Status.Should().Be(OutboxStatus.Processed);
        message.ProcessedAtUtc.Should().Be(Now.AddMinutes(1));
        message.LastError.Should().BeNull("a message that eventually succeeded is not a failure");
    }

    [Fact]
    public void Each_failure_pushes_the_next_attempt_further_out()
    {
        var message = NewMessage();

        message.MarkFailed(Now, "boom");
        var afterFirst = message.NextAttemptAtUtc - Now;

        message.MarkFailed(Now, "boom");
        var afterSecond = message.NextAttemptAtUtc - Now;

        afterFirst.Should().Be(TimeSpan.FromSeconds(2));
        afterSecond.Should().Be(TimeSpan.FromSeconds(4));
        afterSecond.Should().BeGreaterThan(afterFirst);
    }

    [Fact]
    public void Backoff_is_capped_so_a_long_outage_does_not_push_retries_into_next_week()
    {
        OutboxMessage.BackoffFor(attempts: 30).Should().Be(TimeSpan.FromSeconds(600));
    }

    [Fact]
    public void Jitter_spreads_retries_without_ever_shortening_the_base_delay()
    {
        var none = OutboxMessage.BackoffFor(attempts: 3, jitter: 0);
        var full = OutboxMessage.BackoffFor(attempts: 3, jitter: 1);

        none.Should().Be(TimeSpan.FromSeconds(8));
        full.Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public void Jitter_outside_the_unit_interval_is_clamped_rather_than_trusted()
    {
        OutboxMessage.BackoffFor(3, jitter: -5).Should().Be(TimeSpan.FromSeconds(8));
        OutboxMessage.BackoffFor(3, jitter: 99).Should().Be(TimeSpan.FromSeconds(12));
    }

    [Fact]
    public void A_message_is_dead_lettered_once_attempts_run_out()
    {
        var message = NewMessage();

        for (var i = 0; i < OutboxMessage.MaxAttempts; i++)
        {
            message.MarkFailed(Now, "downstream is down");
        }

        message.Status.Should().Be(OutboxStatus.Dead);
        message.Attempts.Should().Be(OutboxMessage.MaxAttempts);
        message.LastError.Should().Be("downstream is down");
    }

    [Fact]
    public void A_very_long_error_is_truncated_so_one_stack_trace_cannot_bloat_the_row()
    {
        var message = NewMessage();

        message.MarkFailed(Now, new string('x', 5000));

        message.LastError!.Length.Should().Be(2000);
    }
}
