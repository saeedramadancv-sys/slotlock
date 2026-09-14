namespace SlotLock.Application.Abstractions;

/// <summary>
/// Performs the side effect an outbox message stands for: send the email, call the webhook,
/// publish to the bus.
/// </summary>
/// <remarks>
/// <para>
/// The seam exists so the dispatcher - which owns batching, backoff and dead-lettering -
/// knows nothing about SMTP or HTTP, and so a test can make delivery fail on demand rather
/// than by unplugging something.
/// </para>
/// <para>
/// <b>Implementations must tolerate being called twice for the same message.</b> Delivery is
/// at-least-once: a crash between a successful send and the row being marked processed is
/// invisible from here, and the only safe assumption on restart is that the send may not
/// have happened. <paramref name="messageId"/> is the stable key to dedupe on.
/// </para>
/// </remarks>
public interface IOutboxMessageHandler
{
    /// <summary>
    /// Handles one message. Throwing marks the attempt failed and schedules a retry; the
    /// dispatcher treats a clean return as delivered.
    /// </summary>
    Task HandleAsync(
        Guid messageId,
        string type,
        string payload,
        CancellationToken cancellationToken = default);
}
