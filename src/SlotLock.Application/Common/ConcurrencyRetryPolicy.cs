using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlotLock.Application.Options;

namespace SlotLock.Application.Common;

/// <summary>
/// Runs an operation that may lose an optimistic-concurrency race, and retries the loser.
/// </summary>
/// <remarks>
/// <para>
/// Optimistic concurrency makes a conflict <i>detectable</i>; it does not make it go away.
/// Detection without a retry moves the problem to the customer, who is refused a seat that
/// was in fact free. This policy closes that gap: on a conflict it reloads and tries again,
/// and gives up only once the seat is genuinely contested.
/// </para>
/// <para>
/// <b>The retry budget has to outlast the contention.</b> The first version of this class
/// allowed five attempts with sub-millisecond pauses, and an integration test caught what
/// that costs: twelve seats, sixty simultaneous callers, and only eleven seats sold. Nothing
/// was oversold - the safety property held - but a caller was refused a seat that existed,
/// because it burned its whole budget in about five milliseconds while the winners were still
/// committing.
/// </para>
/// <para>
/// So the budget is sized against the thing it is waiting for. A loser must stay in the race
/// long enough for the contended row to drain, which takes as long as the queue ahead of it.
/// Ten attempts on the schedule below spans roughly a quarter of a second - far longer than a
/// row takes to settle, and still well inside any sane request timeout.
/// </para>
/// <para>
/// <b>Why the delay is randomised.</b> Two requests that collide and both retry after the
/// same fixed pause collide again, in lockstep, for as long as the pause is deterministic.
/// A random component breaks the phase alignment - the same reason Ethernet backed off
/// randomly.
/// </para>
/// </remarks>
public sealed class ConcurrencyRetryPolicy
{
    /// <summary>
    /// Ceiling on the pause between attempts. Row contention resolves in milliseconds, so
    /// waiting longer than this adds latency without improving the odds.
    /// </summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Base delay for the first retry. Deliberately not sub-millisecond: a pause shorter than
    /// the transaction it is waiting on does not let anyone through, it just spends an
    /// attempt.
    /// </summary>
    private const double BaseDelayMilliseconds = 2;

    private readonly ILogger<ConcurrencyRetryPolicy> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<double> _jitter;

    public ConcurrencyRetryPolicy(
        ILogger<ConcurrencyRetryPolicy> logger,
        TimeProvider timeProvider,
        IOptions<BookingOptions> options,
        Func<double>? jitter = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _timeProvider = timeProvider;
        MaxAttempts = options.Value.MaxConcurrencyAttempts;

        // Injectable so a test can make the schedule deterministic. Random.Shared is
        // thread-safe, which matters because every concurrent request shares this policy.
        _jitter = jitter ?? (() => Random.Shared.NextDouble());
    }

    public int MaxAttempts { get; }

    /// <summary>
    /// Invokes <paramref name="operation"/>, retrying while it reports a concurrency conflict.
    /// </summary>
    /// <param name="operation">
    /// Receives the 1-based attempt number. It must re-read the state it depends on each time
    /// it is called: retrying against the same stale objects reproduces the same conflict
    /// forever, which turns this policy into an expensive way to fail.
    /// </param>
    /// <exception cref="ConcurrencyExhaustedException">
    /// Every attempt conflicted. The caller should surface this as 409 Conflict.
    /// </exception>
    public async Task<T> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        ConcurrencyConflictException? last = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                return await operation(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyConflictException ex)
            {
                last = ex;

                // Debug, not Warning. Under healthy contention this fires constantly and would
                // drown the log in events that describe the system working correctly.
                _logger.LogDebug(
                    "Concurrency conflict on attempt {Attempt} of {MaxAttempts}: {Reason}",
                    attempt,
                    MaxAttempts,
                    ex.Message);

                if (attempt == MaxAttempts)
                {
                    break;
                }

                // Task.Delay overload taking a TimeProvider: a fake clock in a test can
                // advance past this instantly instead of the suite actually sleeping.
                await Task
                    .Delay(BackoffFor(attempt), _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _logger.LogWarning(
            "Giving up after {MaxAttempts} concurrency conflicts; the row is genuinely contested.",
            MaxAttempts);

        throw new ConcurrencyExhaustedException(MaxAttempts, last);
    }

    /// <summary>
    /// Pause before the next attempt: a doubling base, capped, scaled by a random factor.
    /// </summary>
    /// <remarks>
    /// Full jitter - a uniform draw across the whole window rather than a small wobble around
    /// it. It spreads a colliding group the most for a given average delay, which is the
    /// property that matters when the losers are all waiting on the same row.
    /// </remarks>
    internal TimeSpan BackoffFor(int attempt)
    {
        var ceiling = Math.Min(
            BaseDelayMilliseconds * Math.Pow(2, attempt - 1),
            MaxBackoff.TotalMilliseconds);

        return TimeSpan.FromMilliseconds(ceiling * _jitter());
    }
}
