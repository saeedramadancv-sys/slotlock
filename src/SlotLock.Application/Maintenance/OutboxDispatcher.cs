using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlotLock.Application.Abstractions;
using SlotLock.Application.Options;
using SlotLock.Domain.Enums;

namespace SlotLock.Application.Maintenance;

/// <summary>Outcome of one dispatch pass.</summary>
public sealed record OutboxRunResult(int Dispatched, int Failed, int DeadLettered)
{
    public int Total => Dispatched + Failed;
}

/// <summary>
/// Drains pending outbox messages and hands each to a handler.
/// </summary>
/// <remarks>
/// <para>
/// This half of the outbox pattern turns a durable row into an actual side effect. The
/// writer's job was to make the intent survive the transaction; this one's is to keep trying
/// until it happens, and to stop trying once it clearly will not.
/// </para>
/// <para>
/// <b>Failure is contained to one message.</b> A handler that throws marks that row failed
/// and moves on, so one malformed payload cannot stall the queue behind it - the classic
/// head-of-line block, where a single poison message silently stops everyone's email.
/// </para>
/// <para>
/// <b>Progress is saved even when sends fail.</b> The attempt counters and backoff schedule
/// are written at the end of every pass, including a pass where nothing was delivered.
/// Otherwise a permanently failing message would be retried at full speed forever, because
/// nothing recorded that it had already been tried.
/// </para>
/// </remarks>
public sealed class OutboxDispatcher
{
    private readonly IOutboxRepository _outbox;
    private readonly IOutboxMessageHandler _handler;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _time;
    private readonly BookingOptions _options;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly Func<double> _jitter;

    public OutboxDispatcher(
        IOutboxRepository outbox,
        IOutboxMessageHandler handler,
        IUnitOfWork unitOfWork,
        TimeProvider time,
        IOptions<BookingOptions> options,
        ILogger<OutboxDispatcher> logger,
        Func<double>? jitter = null)
    {
        _outbox = outbox;
        _handler = handler;
        _unitOfWork = unitOfWork;
        _time = time;
        _options = options.Value;
        _logger = logger;
        _jitter = jitter ?? (() => Random.Shared.NextDouble());
    }

    public async Task<OutboxRunResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var due = await _outbox
            .ListDueAsync(now, _options.OutboxBatchSize, cancellationToken)
            .ConfigureAwait(false);

        if (due.Count == 0)
        {
            return new OutboxRunResult(0, 0, 0);
        }

        var dispatched = 0;
        var failed = 0;
        var deadLettered = 0;

        foreach (var message in due)
        {
            try
            {
                await _handler
                    .HandleAsync(message.Id, message.Type, message.Payload, cancellationToken)
                    .ConfigureAwait(false);

                message.MarkProcessed(_time.GetUtcNow());
                dispatched++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown, not a delivery failure. Counting it as an attempt would burn a
                // retry - and after enough restarts, dead-letter a message that was never
                // actually tried.
                break;
            }
#pragma warning disable CA1031 // A handler may throw anything; one bad message must not stop the queue.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                message.MarkFailed(_time.GetUtcNow(), ex.Message, _jitter());
                failed++;

                if (message.Status == OutboxStatus.Dead)
                {
                    deadLettered++;

                    // Error, not Warning: nobody is going to retry this one. It needs a human.
                    _logger.LogError(
                        ex,
                        "Outbox message {MessageId} of type {MessageType} dead-lettered after {Attempts} attempts.",
                        message.Id,
                        message.Type,
                        message.Attempts);
                }
                else
                {
                    _logger.LogWarning(
                        ex,
                        "Outbox message {MessageId} failed on attempt {Attempts}; next attempt at {NextAttempt:O}.",
                        message.Id,
                        message.Attempts,
                        message.NextAttemptAtUtc);
                }
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (dispatched > 0 || failed > 0)
        {
            _logger.LogInformation(
                "Outbox pass: {Dispatched} dispatched, {Failed} failed, {DeadLettered} dead-lettered.",
                dispatched,
                failed,
                deadLettered);
        }

        return new OutboxRunResult(dispatched, failed, deadLettered);
    }
}
