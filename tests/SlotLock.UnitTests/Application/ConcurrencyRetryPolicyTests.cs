using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SlotLock.Application.Common;
using SlotLock.Application.Options;

namespace SlotLock.UnitTests.Application;

/// <summary>
/// The retry loop, without a database.
/// </summary>
/// <remarks>
/// Jitter is pinned so the pauses collapse to zero and the suite does not spend real time
/// asleep; the schedule itself is asserted separately, where the randomness is the subject
/// rather than an obstacle.
/// </remarks>
public class ConcurrencyRetryPolicyTests
{
    private static ConcurrencyRetryPolicy Policy(int maxAttempts = 5, Func<double>? jitter = null) =>
        new(
            NullLogger<ConcurrencyRetryPolicy>.Instance,
            new FakeTimeProvider(),
            Options.Create(new BookingOptions { MaxConcurrencyAttempts = maxAttempts }),
            jitter ?? (() => 0));

    [Fact]
    public async Task An_operation_that_succeeds_first_time_is_called_once()
    {
        var calls = 0;

        var result = await Policy().ExecuteAsync((_, _) =>
        {
            calls++;
            return Task.FromResult("done");
        });

        result.Should().Be("done");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_conflict_is_retried_and_the_later_attempt_can_win()
    {
        var calls = 0;

        var result = await Policy().ExecuteAsync((attempt, _) =>
        {
            calls++;
            return attempt < 3
                ? throw new ConcurrencyConflictException("lost the race")
                : Task.FromResult(attempt);
        });

        result.Should().Be(3);
        calls.Should().Be(3);
    }

    [Fact]
    public async Task The_attempt_number_is_passed_through_so_the_caller_can_reload()
    {
        // The operation uses this to know it must discard what it loaded. Getting it wrong
        // means every retry re-reads stale state and conflicts again.
        var seen = new List<int>();

        var act = async () => await Policy(maxAttempts: 4).ExecuteAsync<int>((attempt, _) =>
        {
            seen.Add(attempt);
            throw new ConcurrencyConflictException("still contested");
        });

        await act.Should().ThrowAsync<ConcurrencyExhaustedException>();
        seen.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task Exhausting_every_attempt_reports_how_many_were_made()
    {
        var act = async () => await Policy(maxAttempts: 3).ExecuteAsync<int>((_, _) =>
            throw new ConcurrencyConflictException("contested"));

        var thrown = await act.Should().ThrowAsync<ConcurrencyExhaustedException>();

        thrown.Which.Attempts.Should().Be(3);
        thrown.Which.InnerException.Should().BeOfType<ConcurrencyConflictException>(
            "the original conflict is kept so a log line can say what actually clashed");
    }

    [Fact]
    public async Task Any_other_exception_is_not_retried()
    {
        // A full slot is an answer, not a race. Retrying it would make the caller wait five
        // times as long for the same refusal.
        var calls = 0;

        var act = async () => await Policy().ExecuteAsync<int>((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("not a conflict");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(1);
    }

    [Fact]
    public void The_attempt_budget_comes_from_configuration()
    {
        // It was once hard-coded while the option was bound and ignored, which is worse than
        // having no option at all: the configuration file described behaviour that was not
        // happening.
        Policy(maxAttempts: 12).MaxAttempts.Should().Be(12);
    }

    [Fact]
    public void Backoff_grows_with_each_attempt_and_is_capped()
    {
        var policy = Policy(jitter: () => 1);

        var delays = Enumerable.Range(1, 8).Select(policy.BackoffFor).ToList();

        delays[0].Should().Be(TimeSpan.FromMilliseconds(2));
        delays[1].Should().Be(TimeSpan.FromMilliseconds(4));
        delays[2].Should().Be(TimeSpan.FromMilliseconds(8));

        delays.Should().BeInAscendingOrder();
        delays.Should().OnlyContain(d => d <= ConcurrencyRetryPolicy.MaxBackoff);
    }

    [Fact]
    public void Full_jitter_spreads_a_colliding_group_across_the_whole_window()
    {
        // The point is the spread, not the average. Two requests that back off by the same
        // amount collide again on the next attempt, and keep colliding as long as the pause
        // is deterministic.
        var draws = new Queue<double>([0.0, 0.25, 0.5, 0.75, 0.99]);
        var policy = Policy(jitter: draws.Dequeue);

        var delays = Enumerable.Range(0, 5).Select(_ => policy.BackoffFor(4)).ToList();

        delays.Should().OnlyHaveUniqueItems();
        delays[0].Should().Be(TimeSpan.Zero);
        delays[^1].Should().BeCloseTo(TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(0.5));
    }
}
