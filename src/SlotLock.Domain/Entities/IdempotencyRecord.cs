using SlotLock.Domain.Common;

namespace SlotLock.Domain.Entities;

/// <summary>
/// The stored outcome of a request that carried an Idempotency-Key header.
/// </summary>
/// <remarks>
/// <para>
/// Networks retry. A mobile client that loses its connection after the server committed, but
/// before the response arrived, cannot tell "the booking was not created" apart from "the
/// booking was created and I did not hear about it". Retrying blind creates a second
/// booking; not retrying loses one.
/// </para>
/// <para>
/// The key breaks the tie. The first request to claim it wins by inserting this row under a
/// unique index; the same key arriving again returns the stored response instead of doing
/// the work twice. The row is claimed before the work runs, so two simultaneous retries
/// cannot both proceed - the loser sees the claim and backs off rather than booking.
/// </para>
/// <para>
/// <see cref="RequestFingerprint"/> guards against reuse: the same key sent with a different
/// body is a client bug, and quietly returning the first booking would hide it.
/// </para>
/// </remarks>
public class IdempotencyRecord : BaseEntity
{
    // EF Core materialisation constructor.
    private IdempotencyRecord()
    {
        Key = string.Empty;
        Endpoint = string.Empty;
        RequestFingerprint = string.Empty;
    }

    public IdempotencyRecord(
        string key,
        string endpoint,
        string requestFingerprint,
        DateTimeOffset now,
        TimeSpan retention)
    {
        Key = Guard.AgainstTooLong(Guard.AgainstNullOrWhiteSpace(key, nameof(key)), 200, nameof(key));
        Endpoint = Guard.AgainstNullOrWhiteSpace(endpoint, nameof(endpoint));
        RequestFingerprint = Guard.AgainstNullOrWhiteSpace(requestFingerprint, nameof(requestFingerprint));
        ExpiresAtUtc = now.Add(retention);
        StampCreated(now);
    }

    public string Key { get; private set; }

    /// <summary>
    /// The route the key was used on. Scoping by endpoint means a client that reuses one
    /// request id across two different operations is not told its second call is a duplicate.
    /// </summary>
    public string Endpoint { get; private set; }

    /// <summary>Hash of the request body, to detect the same key sent with different content.</summary>
    public string RequestFingerprint { get; private set; }

    public int? ResponseStatusCode { get; private set; }

    public string? ResponseBody { get; private set; }

    /// <summary>
    /// Null until the original request finishes. A concurrent retry that finds a row in this
    /// state knows the work is still running and must not start it a second time.
    /// </summary>
    public DateTimeOffset? CompletedAtUtc { get; private set; }

    /// <summary>
    /// When the record may be swept. Keys are not kept forever: the table would grow without
    /// bound, and a client retrying a day later is not retrying, it is asking again.
    /// </summary>
    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public bool IsCompleted => CompletedAtUtc is not null;

    public bool Matches(string requestFingerprint) =>
        string.Equals(RequestFingerprint, requestFingerprint, StringComparison.Ordinal);

    public void Complete(int statusCode, string? responseBody, DateTimeOffset now)
    {
        ResponseStatusCode = statusCode;
        ResponseBody = responseBody;
        CompletedAtUtc = now;
    }
}
