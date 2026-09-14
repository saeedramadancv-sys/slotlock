using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SlotLock.Application.Abstractions;
using SlotLock.Application.Common;
using SlotLock.Domain.Common;

namespace SlotLock.Infrastructure.Persistence;

/// <summary>
/// Commits staged work and turns persistence-level failures into the vocabulary the
/// application layer reasons in.
/// </summary>
/// <remarks>
/// This translation is the reason <c>BookingService</c> can be read, and tested, without EF
/// Core anywhere in sight. It is also the one place that knows a SQL Server error number
/// means "a CHECK constraint refused this", so that knowledge does not leak upwards.
/// </remarks>
public sealed class UnitOfWork : IUnitOfWork
{
    /// <summary>Violation of a UNIQUE index or constraint.</summary>
    private const int SqlUniqueIndexViolation = 2601;

    /// <summary>Violation of a UNIQUE KEY constraint.</summary>
    private const int SqlUniqueConstraintViolation = 2627;

    /// <summary>Violation of a CHECK constraint.</summary>
    private const int SqlCheckConstraintViolation = 547;

    private readonly SlotLockDbContext _context;

    public UnitOfWork(SlotLockDbContext context)
    {
        _context = context;
    }

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // The expected outcome of losing a race, not a fault. Rethrown as the
            // application's own type so the retry policy - which knows nothing about EF -
            // can act on it.
            throw new ConcurrencyConflictException(
                "A row was modified by another transaction after it was read.",
                ex);
        }
        catch (DbUpdateException ex) when (IsCheckConstraintViolation(ex))
        {
            // Reaching here means the seat count and its concurrency token both failed to
            // stop an oversell and the schema caught it. That is a bug in this service, but
            // the database refusing the write is the correct outcome, and the caller gets a
            // conflict rather than a confirmed booking that does not fit.
            throw new DomainException(
                "capacity_exceeded",
                "The database refused the write because it would exceed the slot's capacity.");
        }
    }

    /// <summary>
    /// Detaches everything currently tracked.
    /// </summary>
    /// <remarks>
    /// EF Core resolves a query against its identity map first, so a retry without this gets
    /// handed back the same stale entity - including the stale <c>RowVersion</c> that lost
    /// the race - and conflicts again. The loop would burn every attempt and then report a
    /// conflict that a single reload would have resolved.
    /// </remarks>
    public void Reset() => _context.ChangeTracker.Clear();

    internal static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException sql &&
        sql.Errors.Cast<SqlError>().Any(e =>
            e.Number is SqlUniqueIndexViolation or SqlUniqueConstraintViolation);

    private static bool IsCheckConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: SqlCheckConstraintViolation };
}
