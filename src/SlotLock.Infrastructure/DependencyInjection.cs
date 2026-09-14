using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SlotLock.Application.Abstractions;
using SlotLock.Application.Bookings;
using SlotLock.Application.Common;
using SlotLock.Application.Maintenance;
using SlotLock.Application.Options;
using SlotLock.Application.Scheduling;
using SlotLock.Infrastructure.BackgroundServices;
using SlotLock.Infrastructure.Messaging;
using SlotLock.Infrastructure.Persistence;

namespace SlotLock.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers persistence, the application services and the background workers.
    /// </summary>
    /// <param name="runBackgroundWorkers">
    /// False in the integration tests. The sweeper reclaiming a hold, or the dispatcher
    /// draining the outbox, in the middle of a test would make assertions depend on timer
    /// ticks; the tests drive those components directly instead so their behaviour is
    /// asserted rather than waited for.
    /// </param>
    public static IServiceCollection AddSlotLockInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool runBackgroundWorkers = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<BookingOptions>()
            .Bind(configuration.GetSection(BookingOptions.SectionName))
            .ValidateDataAnnotations()
            // Validate at startup, not on first use. A bad interval should fail the deploy
            // while someone is watching, not surface as odd behaviour under load later.
            .ValidateOnStart();

        var connectionString = configuration.GetConnectionString("SlotLock")
            ?? throw new InvalidOperationException(
                "Connection string 'SlotLock' is not configured.");

        void Configure(DbContextOptionsBuilder options) =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly(typeof(DependencyInjection).Assembly.FullName);

                // Retries for transient faults - a failover, a throttled Azure SQL tier.
                // They apply to the connection, not to concurrency conflicts: a lost
                // optimistic race is a logical outcome and is retried by
                // ConcurrencyRetryPolicy against freshly read state, which is the only kind
                // of retry that can actually succeed.
                sql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(5),
                    errorNumbersToAdd: null);
            });

        // Both a scoped context for request work and a factory for the idempotency store,
        // which needs a separate transaction. Registering both requires the shared options to
        // be a singleton.
        services.AddDbContext<SlotLockDbContext>(Configure, optionsLifetime: ServiceLifetime.Singleton);
        services.AddDbContextFactory<SlotLockDbContext>(Configure, lifetime: ServiceLifetime.Singleton);

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IResourceRepository, ResourceRepository>();
        services.AddScoped<ISlotRepository, SlotRepository>();
        services.AddScoped<IBookingRepository, BookingRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddScoped<IOutboxMessageHandler, LoggingOutboxMessageHandler>();

        services.AddScoped<ConcurrencyRetryPolicy>();
        services.AddScoped<BookingService>();
        services.AddScoped<ScheduleService>();
        services.AddScoped<AvailabilityService>();
        services.AddScoped<HoldSweeper>();
        services.AddScoped<OutboxDispatcher>();

        // The real clock. Every component takes time as a dependency, so a test can supply a
        // fake one and exercise hold expiry without the suite actually waiting ten minutes.
        services.TryAddSingletonTimeProvider();

        if (runBackgroundWorkers)
        {
            services.AddHostedService<HoldSweeperWorker>();
            services.AddHostedService<OutboxWorker>();
        }

        return services;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
