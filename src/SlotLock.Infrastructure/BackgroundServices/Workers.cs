using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlotLock.Application.Maintenance;
using SlotLock.Application.Options;

namespace SlotLock.Infrastructure.BackgroundServices;

/// <summary>
/// Reclaims seats from holds that lapsed, and purges spent idempotency records.
/// </summary>
/// <remarks>
/// The two jobs share a tick because both are housekeeping on the same database and neither
/// is urgent; a second timer would double the wake-ups for no benefit.
/// </remarks>
public sealed class HoldSweeperWorker : PeriodicWorker
{
    private readonly BookingOptions _options;

    public HoldSweeperWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<BookingOptions> options,
        ILogger<HoldSweeperWorker> logger)
        : base(scopeFactory, logger)
    {
        _options = options.Value;
    }

    protected override TimeSpan Interval => _options.SweepInterval;

    protected override string WorkerName => "Hold sweeper";

    protected override async Task RunOnceAsync(IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopedServices);

        var sweeper = scopedServices.GetRequiredService<HoldSweeper>();

        await sweeper.SweepAsync(cancellationToken).ConfigureAwait(false);
        await sweeper.PurgeIdempotencyAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Drains the transactional outbox.</summary>
public sealed class OutboxWorker : PeriodicWorker
{
    private readonly BookingOptions _options;

    public OutboxWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<BookingOptions> options,
        ILogger<OutboxWorker> logger)
        : base(scopeFactory, logger)
    {
        _options = options.Value;
    }

    protected override TimeSpan Interval => _options.OutboxPollInterval;

    protected override string WorkerName => "Outbox dispatcher";

    protected override async Task RunOnceAsync(IServiceProvider scopedServices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopedServices);

        var dispatcher = scopedServices.GetRequiredService<OutboxDispatcher>();

        // Keep going while a pass fills its batch: after a downstream outage the backlog can
        // be far larger than one batch, and waiting a full interval between batches would
        // turn a five-second queue into an hour of catching up.
        OutboxRunResult result;
        do
        {
            result = await dispatcher.RunOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        while (result.Total >= _options.OutboxBatchSize && !cancellationToken.IsCancellationRequested);
    }
}
