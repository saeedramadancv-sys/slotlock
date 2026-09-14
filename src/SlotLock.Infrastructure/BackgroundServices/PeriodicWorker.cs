using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SlotLock.Infrastructure.BackgroundServices;

/// <summary>
/// Runs a scoped unit of work on a fixed interval until the host shuts down.
/// </summary>
/// <remarks>
/// <para>
/// Both background jobs in this service - reclaiming lapsed holds and draining the outbox -
/// have the same shape and the same three ways of going wrong, so the shape lives here once.
/// </para>
/// <para>
/// <b>A scope per tick.</b> A hosted service is a singleton; the services it drives are
/// scoped and hold a DbContext, which is neither thread-safe nor meant to live for days.
/// Resolving them once in the constructor is the classic captive-dependency bug: it appears
/// to work, then leaks tracked entities until memory or a stale change tracker gives out.
/// </para>
/// <para>
/// <b>One tick's failure is not the worker's failure.</b> An exception escaping
/// <c>ExecuteAsync</c> stops a <see cref="BackgroundService"/> for the rest of the process's
/// life, silently under the default host settings. A transient database blip would therefore
/// leave holds never reclaimed until someone restarted the service. Each tick is wrapped so
/// the loop survives and the next one tries again.
/// </para>
/// <para>
/// <b>Wait, then work.</b> <see cref="PeriodicTimer"/> does not overlap ticks: if a pass runs
/// longer than the interval, the next one starts late rather than concurrently. Overlapping
/// passes would have two sweepers competing for the same rows and losing to each other.
/// </para>
/// </remarks>
public abstract class PeriodicWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    protected PeriodicWorker(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>How long to wait between passes.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <summary>Name used in log messages.</summary>
    protected abstract string WorkerName { get; }

    /// <summary>One pass, with a scope of its own.</summary>
    protected abstract Task RunOnceAsync(IServiceProvider scopedServices, CancellationToken cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{Worker} started; running every {Interval}.", WorkerName, Interval);

        using var timer = new PeriodicTimer(Interval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await RunOnceAsync(scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // Deliberate: the loop must outlive any single failed pass.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogError(ex, "{Worker} pass failed; retrying at the next interval.", WorkerName);
            }
        }

        _logger.LogInformation("{Worker} stopped.", WorkerName);
    }
}
