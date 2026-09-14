namespace SlotLock.Domain.Enums;

public enum OutboxStatus
{
    /// <summary>Waiting to be dispatched, or waiting out a backoff after a failure.</summary>
    Pending = 0,

    /// <summary>Handed to the downstream side effect successfully.</summary>
    Processed = 1,

    /// <summary>
    /// Out of attempts. Kept in the table rather than deleted so the failure stays visible
    /// and can be replayed by hand once the cause is fixed.
    /// </summary>
    Dead = 2,
}
