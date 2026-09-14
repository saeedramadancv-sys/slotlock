namespace SlotLock.Application.Abstractions;

/// <summary>
/// Outcome of trying to claim an idempotency key.
/// </summary>
/// <param name="IsFirstCaller">
/// True when this caller won the key and should do the work. False when the key was already
/// claimed, in which case <paramref name="StoredStatusCode"/> and
/// <paramref name="StoredBody"/> carry the original response to replay.
/// </param>
public readonly record struct IdempotencyClaim(
    bool IsFirstCaller,
    int? StoredStatusCode,
    string? StoredBody);

/// <summary>
/// Remembers what a request with a given Idempotency-Key returned, so a retry replays the
/// answer instead of repeating the work.
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Attempts to claim <paramref name="key"/> for this request.
    /// </summary>
    /// <remarks>
    /// The claim is written and committed before the work starts. That ordering is the whole
    /// mechanism: if the row were written afterwards, two retries racing each other would
    /// both find nothing, both book, and both then try to record the same key.
    /// </remarks>
    /// <exception cref="Common.IdempotencyKeyReuseException">
    /// The key exists but was used for a different request body.
    /// </exception>
    /// <exception cref="Common.IdempotentRequestInFlightException">
    /// The key exists and the original request has not finished yet.
    /// </exception>
    Task<IdempotencyClaim> ClaimAsync(
        string key,
        string endpoint,
        string requestFingerprint,
        CancellationToken cancellationToken = default);

    /// <summary>Stores the response so later retries can replay it.</summary>
    Task CompleteAsync(
        string key,
        int statusCode,
        string? responseBody,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops a claim whose work failed.
    /// </summary>
    /// <remarks>
    /// Without this, a request that died mid-flight would leave its key claimed and never
    /// completed, and every retry would be told "still in progress" until the retention
    /// window expired - a transient error turned permanent.
    /// </remarks>
    Task ReleaseAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes records past their retention window. Called by the sweeper.</summary>
    Task<int> PurgeExpiredAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken = default);
}
