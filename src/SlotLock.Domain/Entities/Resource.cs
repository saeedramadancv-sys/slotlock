using SlotLock.Domain.Common;

namespace SlotLock.Domain.Entities;

/// <summary>
/// The thing being booked: a doctor, a meeting room, a padel court, a barber's chair.
/// A resource owns the slots that can be reserved against it.
/// </summary>
/// <remarks>
/// The model is deliberately generic. Encoding "clinic" or "salon" into the schema would
/// mean a new table for every vertical; a resource plus its slots expresses all of them,
/// and callers keep their own domain labels in <see cref="Name"/>.
/// </remarks>
public class Resource : BaseEntity
{
    private readonly List<Slot> _slots = [];

    // EF Core materialisation constructor.
    private Resource()
    {
        Name = string.Empty;
        TimeZoneId = "UTC";
    }

    public Resource(string name, string timeZoneId, DateTimeOffset now, int defaultCapacity = 1)
    {
        Name = Guard.AgainstTooLong(Guard.AgainstNullOrWhiteSpace(name, nameof(name)), 120, nameof(name));
        TimeZoneId = Guard.AgainstInvalidTimeZone(timeZoneId, nameof(timeZoneId));
        DefaultCapacity = Guard.AgainstNonPositive(defaultCapacity, nameof(defaultCapacity));
        IsActive = true;
        StampCreated(now);
    }

    public string Name { get; private set; }

    /// <summary>
    /// The resource's own time zone, e.g. "Asia/Amman". Slots are stored in UTC, but a
    /// working day is a local idea: "09:00 to 17:00" has to survive a daylight-saving
    /// change without silently shifting by an hour.
    /// </summary>
    public string TimeZoneId { get; private set; }

    /// <summary>Capacity applied to generated slots when the caller does not state one.</summary>
    public int DefaultCapacity { get; private set; }

    /// <summary>
    /// When false the resource is hidden from availability and rejects new bookings.
    /// Existing bookings are left alone: deactivating a room must not silently cancel
    /// meetings people already scheduled in it.
    /// </summary>
    public bool IsActive { get; private set; }

    public IReadOnlyCollection<Slot> Slots => _slots.AsReadOnly();

    public void Rename(string name) =>
        Name = Guard.AgainstTooLong(Guard.AgainstNullOrWhiteSpace(name, nameof(name)), 120, nameof(name));

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    public TimeZoneInfo TimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
}
