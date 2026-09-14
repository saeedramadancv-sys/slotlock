using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SlotLock.Infrastructure.Persistence;

namespace SlotLock.IntegrationTests.Infrastructure;

/// <summary>
/// Boots the real API against a real SQL Server database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not an in-memory provider.</b> Everything this suite exists to prove lives in the
/// database: the <c>rowversion</c> token that detects a lost update, the CHECK constraint
/// that refuses an oversell, the unique index that arbitrates an idempotency claim. The
/// in-memory provider implements none of them, so a suite built on it would pass while the
/// deployed service overbooked. A test that cannot fail the way production fails is not
/// evidence.
/// </para>
/// <para>
/// The background workers are switched off. A sweeper reclaiming a hold partway through an
/// assertion would make results depend on timer ticks; the tests drive those components
/// directly instead, which asserts their behaviour rather than waiting for it.
/// </para>
/// </remarks>
public sealed class SlotLockApiFactory : WebApplicationFactory<Program>
{
    public SlotLockApiFactory(string connectionString) => ConnectionString = connectionString;

    public string ConnectionString { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(Environments.Production);

        // UseSetting, not ConfigureAppConfiguration. The application builds its own
        // configuration inside Program.cs, and an in-memory source added through the web host
        // builder is layered underneath appsettings.json rather than over it - so the tests
        // would quietly run against the development database named there. Host settings sit
        // above the file, which is the only placement that reliably wins.
        //
        // Worth knowing because the failure is silent: the suite passes, and it passes while
        // pointed at the wrong database.
        builder.UseSetting("ConnectionStrings:SlotLock", ConnectionString);

        // Hold duration is left at the configured value. A test that needs a lapsed hold
        // seeds one with an expiry already in the past, which is deterministic; shortening
        // the window here would instead make the suite race the clock.

        builder.ConfigureServices(services =>
        {
            // The hosted services are registered by the infrastructure module; removing their
            // descriptors is how a test opts out without the production wiring growing a
            // test-only branch.
            var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
            foreach (var descriptor in hosted)
            {
                services.Remove(descriptor);
            }
        });
    }

    /// <summary>A scope whose services resolve against the test database.</summary>
    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    public SlotLockDbContext CreateDbContext() =>
        Services.GetRequiredService<IDbContextFactory<SlotLockDbContext>>().CreateDbContext();
}
