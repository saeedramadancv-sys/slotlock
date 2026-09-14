namespace SlotLock.Application.Common;

/// <summary>
/// A row this operation depends on was changed by someone else between the read and the
/// write, so the write was rejected rather than applied on top of stale state.
/// </summary>
/// <remarks>
/// <para>
/// This is the application's own type, not the persistence library's. The retry loop and
/// the booking service both reason about conflicts, and neither should have to reference
/// EF Core to do it; the infrastructure layer translates
/// <c>DbUpdateConcurrencyException</c> into this on its way out.
/// </para>
/// <para>
/// A conflict is normal traffic, not a fault. Two people pressing Book at the same instant
/// produce one of these every time, and the correct response is to retry the loser against
/// fresh state - not to log an error.
/// </para>
/// </remarks>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// A conflict that survived every retry. Distinct from
/// <see cref="ConcurrencyConflictException"/> because the caller must be told to try again
/// themselves rather than being left waiting while the server loops.
/// </summary>
public sealed class ConcurrencyExhaustedException : Exception
{
    public ConcurrencyExhaustedException(int attempts, Exception? inner = null)
        : base($"The operation still conflicted after {attempts} attempts.", inner)
    {
        Attempts = attempts;
    }

    public int Attempts { get; }
}

/// <summary>The entity the request named does not exist.</summary>
public sealed class NotFoundException : Exception
{
    public NotFoundException(string entity, object key)
        : base($"{entity} '{key}' was not found.")
    {
        Entity = entity;
        Key = key;
    }

    public string Entity { get; }

    public object Key { get; }
}

/// <summary>
/// The same Idempotency-Key was replayed with a different request body.
/// </summary>
/// <remarks>
/// Returning the first response would be wrong - the client asked for something else - and
/// doing the work would defeat the key. Refusing tells them their key generation is buggy.
/// </remarks>
public sealed class IdempotencyKeyReuseException : Exception
{
    public IdempotencyKeyReuseException(string key)
        : base($"Idempotency key '{key}' was already used for a different request body.")
    {
        Key = key;
    }

    public string Key { get; }
}

/// <summary>
/// A request with this key is still running. The client retried before the first attempt
/// answered; the honest reply is "ask again shortly", not a second booking.
/// </summary>
public sealed class IdempotentRequestInFlightException : Exception
{
    public IdempotentRequestInFlightException(string key)
        : base($"A request with idempotency key '{key}' is still in progress.")
    {
        Key = key;
    }

    public string Key { get; }
}
