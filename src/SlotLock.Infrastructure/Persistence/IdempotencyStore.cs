using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SlotLock.Application.Abstractions;
using SlotLock.Application.Common;
using SlotLock.Application.Options;
using SlotLock.Domain.Entities;

namespace SlotLock.Infrastructure.Persistence;

/// <summary>
/// Stores request outcomes against their Idempotency-Key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this uses its own DbContext.</b> Claiming a key has to commit on its own, before
/// the booking runs, so that a racing retry can see the claim. Sharing the request's context
/// would mean the claim's <c>SaveChanges</c> also flushed whatever the booking had staged so
/// far - committing half a booking to publish a claim. A separate short-lived context from
/// the factory keeps the two transactions genuinely separate.
/// </para>
/// <para>
/// <b>Why the claim is an INSERT and not a SELECT-then-INSERT.</b> Checking whether the key
/// exists and then inserting it has a gap in the middle, which is the same
/// time-of-check-to-time-of-use race the key was introduced to remove. Instead both racers
/// insert; the unique index picks the winner and the loser reads the outcome. The database
/// arbitrates, because it is the only participant that can.
/// </para>
/// </remarks>
public sealed class IdempotencyStore : IIdempotencyStore
{
    private readonly IDbContextFactory<SlotLockDbContext> _contextFactory;
    private readonly TimeProvider _time;
    private readonly BookingOptions _options;

    public IdempotencyStore(
        IDbContextFactory<SlotLockDbContext> contextFactory,
        TimeProvider time,
        IOptions<BookingOptions> options)
    {
        _contextFactory = contextFactory;
        _time = time;
        _options = options.Value;
    }

    public async Task<IdempotencyClaim> ClaimAsync(
        string key,
        string endpoint,
        string requestFingerprint,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var now = _time.GetUtcNow();
        var record = new IdempotencyRecord(key, endpoint, requestFingerprint, now, _options.IdempotencyRetention);
        context.IdempotencyRecords.Add(record);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new IdempotencyClaim(IsFirstCaller: true, StoredStatusCode: null, StoredBody: null);
        }
        catch (DbUpdateException ex) when (UnitOfWork.IsUniqueViolation(ex))
        {
            // Somebody else owns the key. Read what they did with it.
            context.ChangeTracker.Clear();

            var existing = await context.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Key == key && r.Endpoint == endpoint, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                // The row was there long enough to reject the insert and gone by the time it
                // was read - the purge ran in between. Treating that as a first claim is
                // right: nothing recorded remains, so there is nothing to replay.
                return new IdempotencyClaim(IsFirstCaller: true, StoredStatusCode: null, StoredBody: null);
            }

            if (!existing.Matches(requestFingerprint))
            {
                throw new IdempotencyKeyReuseException(key);
            }

            if (!existing.IsCompleted)
            {
                throw new IdempotentRequestInFlightException(key);
            }

            return new IdempotencyClaim(
                IsFirstCaller: false,
                StoredStatusCode: existing.ResponseStatusCode,
                StoredBody: existing.ResponseBody);
        }
    }

    public async Task CompleteAsync(
        string key,
        int statusCode,
        string? responseBody,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        var record = await context.IdempotencyRecords
            .FirstOrDefaultAsync(r => r.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return;
        }

        record.Complete(statusCode, responseBody, _time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // Only an incomplete claim is dropped. A completed one is the stored answer, and
        // deleting it would let a retry re-run work that already succeeded.
        await context.IdempotencyRecords
            .Where(r => r.Key == key && r.CompletedAtUtc == null)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> PurgeExpiredAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        // Set-based delete: loading rows only to delete them would pull the whole expired
        // backlog into memory to throw it away.
        return await context.IdempotencyRecords
            .Where(r => r.ExpiresAtUtc < nowUtc)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
