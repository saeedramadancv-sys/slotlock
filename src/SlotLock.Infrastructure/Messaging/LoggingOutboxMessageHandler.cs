using Microsoft.Extensions.Logging;
using SlotLock.Application.Abstractions;

namespace SlotLock.Infrastructure.Messaging;

/// <summary>
/// The default outbox handler: it logs the message and reports success.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam where a real transport goes - SendGrid, a webhook, a queue. It is a
/// logger here rather than a half-written SMTP client because the interesting engineering in
/// this service is on the other side of the boundary: writing the intent durably inside the
/// booking transaction, retrying it with backoff, and dead-lettering it when it will never
/// succeed. All of that is exercised and tested regardless of what sits behind this
/// interface.
/// </para>
/// <para>
/// Swapping in a real provider is one registration change and no alteration to the
/// dispatcher, which is the point of the abstraction.
/// </para>
/// </remarks>
public sealed class LoggingOutboxMessageHandler : IOutboxMessageHandler
{
    private readonly ILogger<LoggingOutboxMessageHandler> _logger;

    public LoggingOutboxMessageHandler(ILogger<LoggingOutboxMessageHandler> logger) => _logger = logger;

    public Task HandleAsync(
        Guid messageId,
        string type,
        string payload,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Dispatched outbox message {MessageId} of type {MessageType}: {Payload}",
            messageId,
            type,
            payload);

        return Task.CompletedTask;
    }
}
