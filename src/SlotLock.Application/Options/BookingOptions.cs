using System.ComponentModel.DataAnnotations;

namespace SlotLock.Application.Options;

/// <summary>
/// Tunables for the booking pipeline. Bound from configuration and validated at startup, so
/// a nonsensical value fails the deployment instead of surfacing as strange behaviour hours
/// later.
/// </summary>
public sealed class BookingOptions
{
    public const string SectionName = "Booking";

    /// <summary>
    /// How long a held seat stays reserved without confirmation.
    /// </summary>
    /// <remarks>
    /// The trade-off is direct. Too short and a customer entering card details loses the
    /// seat mid-payment. Too long and a browser closed at the payment page keeps a seat out
    /// of circulation, which on a busy resource looks identical to being fully booked. Ten
    /// minutes covers a card payment with a 3-D Secure detour.
    /// </remarks>
    /// <remarks>
    /// The upper bound is written "1.00:00:00" rather than "24:00:00". TimeSpan reads a bare
    /// "24:00:00" as twenty-four <i>days</i>, not hours, because the hour field only spans
    /// 0-23 and the leading number is then taken as days. The same spelling in appsettings
    /// once set idempotency retention to 24 days; the days component is always written out
    /// here so the value cannot be misread.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:30", "1.00:00:00")]
    public TimeSpan HoldDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Attempts before a contested booking is reported as a conflict.
    /// </summary>
    /// <remarks>
    /// Sized against expected contention, not picked for neatness. A loser has to stay in the
    /// race until the contended row drains, so the budget must outlast the queue ahead of it.
    /// At five, an integration test with sixty callers competing for twelve seats sold only
    /// eleven: nothing was oversold, but a caller was refused a seat that was free. Ten
    /// attempts on the policy's backoff schedule spans roughly a quarter of a second, which
    /// clears that case with room to spare.
    /// </remarks>
    [Range(1, 50)]
    public int MaxConcurrencyAttempts { get; set; } = 10;

    /// <summary>
    /// How long a completed idempotency key is honoured.
    /// </summary>
    /// <remarks>
    /// Long enough to cover any retry a client or proxy would sensibly make, short enough
    /// that the table does not grow without bound. Twenty-four hours is the window Stripe
    /// uses, and clients already expect that shape of behaviour.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:01:00", "7.00:00:00")]
    public TimeSpan IdempotencyRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often lapsed holds are reclaimed.</summary>
    /// <remarks>
    /// Seats come back at most this late. It does not affect correctness - a lapsed hold can
    /// never be confirmed regardless of whether the sweeper has run - only how quickly
    /// capacity is visible again.
    /// </remarks>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Lapsed holds reclaimed per sweep.</summary>
    [Range(1, 10_000)]
    public int SweepBatchSize { get; set; } = 200;

    /// <summary>How often the outbox is polled for due messages.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Messages dispatched per poll.</summary>
    [Range(1, 1_000)]
    public int OutboxBatchSize { get; set; } = 50;

    /// <summary>
    /// Longest window the availability endpoint will answer in one call.
    /// </summary>
    /// <remarks>
    /// Without a ceiling a single request can ask for a decade and turn into a table scan
    /// that starves everyone else. Clients page instead.
    /// </remarks>
    [Range(typeof(TimeSpan), "01:00:00", "365.00:00:00")]
    public TimeSpan MaxAvailabilityWindow { get; set; } = TimeSpan.FromDays(62);
}
