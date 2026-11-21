using Framework.Persistence.PostgreSql;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

/// <summary>
/// P0-5: migrations as a separate step. "--migration-status" lists pending migrations (exit code 2),
/// "--migrate-only" applies them (exit code 0), and an instance with Database:MigrateOnStartup=false waits for the
/// migrate step instead of migrating, failing with a clear message when it never comes. Runs on a fresh database.
/// </summary>
sealed partial class Tests
{
    [Test]
    public async Task Migrations_run_as_a_separate_step_and_instances_wait_for_them()
    {
        var pg = Environment.GetEnvironmentVariable("FROGFRAME_TEST_PG");
        if (string.IsNullOrEmpty(pg)) return; // runs with a PostgreSQL test database

        Assert.True(MigrationCommand.Requested(["--migrate-only"]) && MigrationCommand.Requested(["x", "--migration-status"]) && !MigrationCommand.Requested(["--urls", "x"]),
            "command-line detection");

        var dbName = "ff_migtest_" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = new NpgsqlConnection(pg))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {dbName}", admin);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var fresh = new NpgsqlConnectionStringBuilder(pg) { Database = dbName, Pooling = false }.ConnectionString;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddFrogFrameworkPostgreSql(fresh);
            await using (var sp = services.BuildServiceProvider())
            {
                var migrator = sp.GetRequiredService<SchemaMigrator>();

                var all = await migrator.PendingAsync();
                Assert.True(all.Count > 10 && all[0].Version == 1 && all.Select(m => m.Version).SequenceEqual(all.Select(m => m.Version).Order()),
                    $"a new database has every migration pending, in order ({all.Count})");

                // An instance that does not migrate on startup waits, then fails with instructions.
                var waitEx = await Assert.Throws<InvalidOperationException>(() => migrator.WaitUntilMigratedAsync(TimeSpan.Zero), "no migrate step: wait fails");
                Assert.True(waitEx.Message.Contains("--migrate-only") && waitEx.Message.Contains("Database:MigrateOnStartup=false"), waitEx.Message);

                var status = new StringWriter();
                Assert.Equal(MigrationCommand.PendingExitCode, await MigrationCommand.RunAsync(migrator, ["--migration-status"], status), "status: pending");
                Assert.True(status.ToString().Contains($"Pending migration {all[0].Version:000} {all[0].Name}"), status.ToString());
                Assert.Equal(all.Count, (await migrator.PendingAsync()).Count, "status changes nothing");

                var run = new StringWriter();
                Assert.Equal(0, await MigrationCommand.RunAsync(migrator, ["--migrate-only"], run), "migrate-only succeeds: " + run);
                Assert.True(run.ToString().Contains($"Applied {all.Count} migration(s)"), run.ToString());
                Assert.Equal(0, (await migrator.PendingAsync()).Count, "nothing pending afterwards");

                var again = new StringWriter();
                Assert.Equal(0, await MigrationCommand.RunAsync(migrator, ["--migrate-only"], again), "second run is a no-op");
                Assert.True(again.ToString().Contains("up to date"), again.ToString());

                // Configuration errors fail at once instead of being retried like a database that is still starting.
                var wrongServices = new ServiceCollection();
                wrongServices.AddLogging();
                wrongServices.AddFrogFrameworkPostgreSql(new NpgsqlConnectionStringBuilder(pg) { Database = dbName + "_missing", Pooling = false }.ConnectionString);
                await using (var wrong = wrongServices.BuildServiceProvider())
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var unknown = await Assert.Throws<PostgresException>(() => wrong.GetRequiredService<SchemaMigrator>().PendingAsync(), "unknown database");
                    Assert.True(unknown.SqlState == "3D000" && clock.Elapsed < TimeSpan.FromSeconds(10), $"fails fast ({clock.Elapsed.TotalSeconds:0.0}s, {unknown.SqlState})");
                }

                // A waiting instance now proceeds; two migrate steps at once are serialised by the advisory lock.
                await migrator.WaitUntilMigratedAsync(TimeSpan.Zero);
                await Task.WhenAll(migrator.MigrateAsync(), migrator.MigrateAsync());
                Assert.Equal(0, (await migrator.PendingAsync()).Count, "concurrent migrate steps");
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(pg);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
