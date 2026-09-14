using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using SlotLock.IntegrationTests.Infrastructure;

namespace SlotLock.IntegrationTests;

/// <summary>
/// The shape of the JSON on the wire.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the API shipped, briefly, unable to accept its own documented request.
/// <c>"days":["Sunday"]</c> was rejected as unconvertible, and a booking's status came back as
/// <c>0</c>. Every test passed: the unit tests never touched a serialiser, and the integration
/// tests happened to use only endpoints that carried no enum.
/// </para>
/// <para>
/// Ordinals are also the more dangerous default. They are positional, so inserting a member
/// into the middle of an enum silently changes the meaning of every value already stored and
/// every value already sent, and nothing anywhere reports an error while it happens.
/// </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class JsonContractTests : IAsyncLifetime
{
    /// <summary>A Jordanian working week. Hoisted so the array is allocated once.</summary>
    private static readonly string[] WorkingWeek =
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday"];

    private readonly DatabaseFixture _fixture;

    public JsonContractTests(DatabaseFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Weekdays_are_accepted_by_name()
    {
        var resource = await Seed.ResourceAsync(_fixture);
        var client = _fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"/api/v1/resources/{resource.Id}/slots",
            new
            {
                fromDate = "2026-10-04",   // Sunday
                toDate = "2026-10-10",     // Saturday
                dailyStart = "09:00",
                dailyEnd = "17:00",
                slotMinutes = 30,
                days = WorkingWeek,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<SlotPlanResponse>(TestJson.Options);
        result!.Created.Should().Be(80, "five working days at sixteen half-hour slots each");
    }

    [Fact]
    public async Task Booking_status_is_a_name_not_a_number()
    {
        var slot = await Seed.SlotAsync(_fixture, capacity: 1);
        var client = _fixture.Factory.CreateClient();

        var held = await client.PostAsJsonAsync(
            "/api/v1/bookings",
            new { slotId = slot, customerReference = "saeed@example.com" });

        // Read as raw text: deserialising into the DTO would convert either spelling and hide
        // exactly the thing being asserted.
        var body = await held.Content.ReadAsStringAsync();

        body.Should().Contain("\"status\":\"Held\"");
        body.Should().NotContain("\"status\":0");
    }

    [Fact]
    public async Task A_resource_time_zone_is_applied_when_slots_are_generated()
    {
        // Amman is UTC+3 all year - Jordan stopped changing its clocks in 2022 - so an 09:00
        // local opening is 06:00Z. Asserted because a service that silently treats local
        // times as UTC looks completely healthy until someone arrives three hours early.
        var resource = await Seed.ResourceAsync(_fixture, "Asia/Amman");
        var client = _fixture.Factory.CreateClient();

        await client.PostAsJsonAsync(
            $"/api/v1/resources/{resource.Id}/slots",
            new
            {
                fromDate = "2026-10-04",
                toDate = "2026-10-04",
                dailyStart = "09:00",
                dailyEnd = "10:00",
                slotMinutes = 30,
            });

        var availability = await client.GetFromJsonAsync<List<SlotAvailabilityResponse>>(
            $"/api/v1/resources/{resource.Id}/availability" +
            "?from=2026-10-04T00:00:00Z&to=2026-10-05T00:00:00Z",
            TestJson.Options);

        var slots = availability.Should().NotBeNull().And.HaveCount(2).And.Subject.ToList();

        slots[0].StartUtc.Should().Be(new DateTimeOffset(2026, 10, 4, 6, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_refusal_carries_a_stable_machine_readable_code()
    {
        // Clients branch on this. The human-readable title is free to change; the code is the
        // contract, and a client that has to match on English prose is a client that breaks
        // when someone improves the wording.
        var slot = await Seed.SlotAsync(_fixture, capacity: 1);
        var client = _fixture.Factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/bookings", new { slotId = slot, customerReference = "first@example.com" });
        var refused = await client.PostAsJsonAsync("/api/v1/bookings", new { slotId = slot, customerReference = "second@example.com" });

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await refused.Content.ReadFromJsonAsync<ProblemResponse>(TestJson.Options);

        problem!.Code.Should().Be("slot_full");
        problem.Type.Should().Be("https://slotlock.dev/problems/slot_full");
        problem.TraceId.Should().NotBeNullOrWhiteSpace("a caller reporting a problem needs something to quote");
    }

    private sealed record SlotPlanResponse(int Created, int SkippedExisting, int SkippedNonexistentLocalTime);

    private sealed record SlotAvailabilityResponse(Guid SlotId, DateTimeOffset StartUtc, int Capacity, int Remaining);

    private sealed record ProblemResponse(string Type, string Title, int Status, string Code, string TraceId);
}
