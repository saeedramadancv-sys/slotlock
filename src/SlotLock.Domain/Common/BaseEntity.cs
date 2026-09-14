namespace SlotLock.Domain.Common;

/// <summary>
/// Identity and creation stamp shared by every persisted entity.
/// </summary>
/// <remarks>
/// The id is a GUID generated in the application rather than by the database. A booking
/// has to be written together with its outbox message inside one transaction, and the
/// outbox payload needs the booking id; waiting for a database-assigned identity would
/// force a round trip in the middle of that transaction.
/// <para>
/// GUIDs are sequential (<see cref="Guid.CreateVersion7()"/>) so clustered-index inserts
/// stay at the end of the B-tree instead of scattering across pages and fragmenting it.
/// </para>
/// </remarks>
public abstract class BaseEntity
{
    protected BaseEntity()
    {
        Id = Guid.CreateVersion7();
    }

    public Guid Id { get; protected set; }

    public DateTimeOffset CreatedAtUtc { get; protected set; }

    /// <summary>
    /// Stamps the creation time. Separate from the constructor because entities receive
    /// the current instant from a <see cref="TimeProvider"/> rather than reading the
    /// machine clock, which keeps them testable.
    /// </summary>
    protected void StampCreated(DateTimeOffset now) => CreatedAtUtc = now;
}
