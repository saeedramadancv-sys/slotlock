using SlotLock.Domain.Entities;

namespace SlotLock.IntegrationTests.Infrastructure;

/// <summary>
/// Builds the minimum a test needs, straight through the DbContext.
/// </summary>
/// <remarks>
/// Fixtures are inserted directly rather than through the API. Going through HTTP would make
/// every concurrency test depend on the correctness of resource creation and slot generation,
/// so a bug there would fail tests about booking and send the reader to the wrong file.
/// </remarks>
public static class Seed
{
    /// <summary>A resource with one slot starting an hour from now, and its slot id.</summary>
    public static async Task<Guid> SlotAsync(DatabaseFixture fixture, int capacity, TimeSpan? startsIn = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        var now = DateTimeOffset.UtcNow;
        var start = now.Add(startsIn ?? TimeSpan.FromHours(1));

        await using var context = fixture.Factory.CreateDbContext();

        var resource = new Resource("Test resource", "Asia/Amman", now, capacity);
        var slot = new Slot(resource.Id, start, start.AddMinutes(30), capacity, now);

        context.Resources.Add(resource);
        context.Slots.Add(slot);
        await context.SaveChangesAsync();

        return slot.Id;
    }

    public static async Task<Resource> ResourceAsync(DatabaseFixture fixture, string timeZoneId = "Asia/Amman")
    {
        ArgumentNullException.ThrowIfNull(fixture);

        await using var context = fixture.Factory.CreateDbContext();

        var resource = new Resource("Test resource", timeZoneId, DateTimeOffset.UtcNow);
        context.Resources.Add(resource);
        await context.SaveChangesAsync();

        return resource;
    }
}
