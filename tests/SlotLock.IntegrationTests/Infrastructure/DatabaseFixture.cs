using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Respawn;
using SlotLock.Infrastructure.Persistence;

namespace SlotLock.IntegrationTests.Infrastructure;

/// <summary>
/// Creates the test database once, migrates it, and gives each test a clean slate.
/// </summary>
/// <remarks>
/// <para>
/// The schema is applied by running the real migrations rather than
/// <c>EnsureCreated</c>. That way a migration which builds a subtly different schema from the
/// model - a missing CHECK constraint, an index that never got added - fails here instead of
/// on the first deploy.
/// </para>
/// <para>
/// Between tests the tables are emptied rather than dropped and rebuilt. Rebuilding a schema
/// per test turns a fast suite into a slow one, and the suite has to stay fast enough that
/// someone actually runs it before pushing.
/// </para>
/// </remarks>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string DefaultServer = @"Server=.\SQLEXPRESS;Trusted_Connection=True;TrustServerCertificate=True";
    private const string DatabaseName = "SlotLock_Tests";

    private Respawner? _respawner;

    public string ConnectionString { get; private set; } = string.Empty;

    public SlotLockApiFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // CI runs SQL Server in a container with its own credentials; a developer runs the
        // local instance. The environment variable is how the same suite serves both.
        var server = Environment.GetEnvironmentVariable("SLOTLOCK_TEST_SQL") ?? DefaultServer;

        ConnectionString = new SqlConnectionStringBuilder(server)
        {
            InitialCatalog = DatabaseName,
        }.ConnectionString;

        Factory = new SlotLockApiFactory(ConnectionString);

        await using var context = Factory.CreateDbContext();
        await context.Database.MigrateAsync();

        _respawner = await Respawner.CreateAsync(ConnectionString, new RespawnerOptions
        {
            // Left alone: wiping it would make EF believe the schema is unapplied and try to
            // migrate an already-migrated database on the next run.
            TablesToIgnore = ["__EFMigrationsHistory"],
        });
    }

    /// <summary>Empties every table. Called at the start of each test.</summary>
    public Task ResetAsync() => _respawner!.ResetAsync(ConnectionString);

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }
    }
}

/// <summary>
/// Shares one database and one booted API across the suite.
/// </summary>
/// <remarks>
/// Also serialises the tests. They share a database, and xUnit would otherwise run classes in
/// parallel - so one test's reset would wipe another's fixtures mid-run, producing failures
/// that look like real bugs and never reproduce alone.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
