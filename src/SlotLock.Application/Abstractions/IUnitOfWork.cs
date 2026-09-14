namespace SlotLock.Application.Abstractions;

/// <summary>
/// The transactional boundary. Everything staged through the repositories is written by one
/// <see cref="SaveChangesAsync"/> call, which is what lets a booking and its outbox message
/// commit together or not at all.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Commits every staged change as one transaction.
    /// </summary>
    /// <exception cref="Common.ConcurrencyConflictException">
    /// A row carrying a concurrency token was modified since it was read. The caller should
    /// let <see cref="Common.ConcurrencyRetryPolicy"/> reload and try again.
    /// </exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Discards everything loaded and staged so far, so the next read goes to the database.
    /// </summary>
    /// <remarks>
    /// A retry after a concurrency conflict has to start from current state. Without this,
    /// the second attempt re-reads from the identity map, gets the same stale row and the
    /// same stale version token, and conflicts again - the loop would spin and fail with a
    /// conflict that was resolvable.
    /// </remarks>
    void Reset();
}
