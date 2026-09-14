using SlotLock.Domain.Common;
using SlotLock.Domain.Enums;

namespace SlotLock.Domain.Entities;

/// <summary>
/// A side effect that must happen because a transaction committed - a confirmation email,
/// a webhook, a message on a bus.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this table exists.</b> Sending the email inside the request handler couples two
/// systems that fail independently. Send before the commit and a rolled-back booking still
/// emails the customer. Send after, and a process that dies in between loses the email with
/// no record that it was ever owed. Neither ordering is safe, because a database transaction
/// and an SMTP call cannot be made atomic.
/// </para>
/// <para>
/// The outbox takes the second system out of the transaction entirely. The booking and this
/// row are written by the same SaveChanges, so they commit or roll back together. A separate
/// dispatcher then reads pending rows and performs the side effect. The handoff is
/// at-least-once: a crash after sending but before marking the row processed will send
/// again, which is why the payload carries a stable message id for consumers to dedupe on.
/// </para>
/// </remarks>
public class OutboxMessage : BaseEntity
{
    /// <summary>
    /// Attempts allowed before the message is dead-lettered. Six attempts with the backoff
    /// below spans roughly half an hour, which covers a downstream restart or a short
    /// network partition without retrying a genuinely poisoned message forever.
    /// </summary>
    public const int MaxAttempts = 6;

    // EF Core materialisation constructor.
    private OutboxMessage()
    {
        Type = string.Empty;
        Payload = string.Empty;
    }

    public OutboxMessage(string type, string payload, DateTimeOffset now)
    {
        Type = Guard.AgainstTooLong(Guard.AgainstNullOrWhiteSpace(type, nameof(type)), 200, nameof(type));
        Payload = Guard.AgainstNullOrWhiteSpace(payload, nameof(payload));
        Status = OutboxStatus.Pending;
        NextAttemptAtUtc = now;
        StampCreated(now);
    }

    /// <summary>Logical message name, for example booking.confirmed.</summary>
    public string Type { get; private set; }

    /// <summary>Serialised event body. Opaque to the dispatcher.</summary>
    public string Payload { get; private set; }

    public OutboxStatus Status { get; private set; }

    public int Attempts { get; private set; }

    /// <summary>
    /// Earliest time the dispatcher may pick this row up. Also how backoff is expressed: a
    /// failed attempt pushes this forward instead of blocking a worker thread on a sleep.
    /// </summary>
    public DateTimeOffset NextAttemptAtUtc { get; private set; }

    public DateTimeOffset? ProcessedAtUtc { get; private set; }

    public string? LastError { get; private set; }

    public void MarkProcessed(DateTimeOffset now)
    {
        Status = OutboxStatus.Processed;
        ProcessedAtUtc = now;
        Attempts++;
        LastError = null;
    }

    /// <summary>
    /// Records a failed attempt and schedules the next one, or dead-letters the message once
    /// <see cref="MaxAttempts"/> is reached.
    /// </summary>
    /// <param name="jitter">
    /// A value in [0,1) mixed into the delay. Without it, a batch of messages that failed
    /// together retries together, and the downstream service is hit by the same thundering
    /// herd that knocked it over. Passed in rather than generated here so the schedule is
    /// deterministic under test.
    /// </param>
    public void MarkFailed(DateTimeOffset now, string error, double jitter = 0)
    {
        Attempts++;
        LastError = Truncate(error, 2000);

        if (Attempts >= MaxAttempts)
        {
            Status = OutboxStatus.Dead;
            return;
        }

        NextAttemptAtUtc = now.Add(BackoffFor(Attempts, jitter));
    }

    /// <summary>
    /// Exponential backoff: two to the power of the attempt count, in seconds, capped at ten
    /// minutes, plus up to half the base delay as jitter.
    /// </summary>
    public static TimeSpan BackoffFor(int attempts, double jitter = 0)
    {
        var seconds = Math.Min(Math.Pow(2, attempts), 600);
        return TimeSpan.FromSeconds(seconds + (seconds * 0.5 * Math.Clamp(jitter, 0, 1)));
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
