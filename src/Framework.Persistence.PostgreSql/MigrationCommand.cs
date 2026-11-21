namespace Framework.Persistence.PostgreSql;

/// <summary>
/// Command-line migration step, separate from the web application's startup (rolling updates with several instances):
/// <c>--migrate-only</c> applies pending migrations and exits; <c>--migration-status</c> only lists them.
/// The web server is not started in either mode. See docs/guides/database-migrations.md.
/// </summary>
public static class MigrationCommand
{
    public const string MigrateOnly = "--migrate-only";
    public const string Status = "--migration-status";

    /// <summary>Exit code when <c>--migration-status</c> finds pending migrations.</summary>
    public const int PendingExitCode = 2;

    /// <summary>True when the arguments ask for a migration command instead of the web application.</summary>
    public static bool Requested(IEnumerable<string> args) => args.Any(a => a is MigrateOnly or Status);

    /// <summary>
    /// Runs the requested command and returns the process exit code: 0 = schema up to date (after migrating, with
    /// <c>--migrate-only</c>), 2 = migrations pending (<c>--migration-status</c>), 1 = failure.
    /// </summary>
    public static async Task<int> RunAsync(SchemaMigrator migrator, IEnumerable<string> args, TextWriter output, CancellationToken ct = default)
    {
        var migrate = args.Contains(MigrateOnly);
        var pending = await migrator.PendingAsync(ct);
        if (pending.Count == 0)
        {
            output.WriteLine("Database schema is up to date.");
            return 0;
        }
        foreach (var (version, name) in pending) output.WriteLine($"Pending migration {version:000} {name}");
        if (!migrate) return PendingExitCode;

        await migrator.MigrateAsync(ct);
        var left = await migrator.PendingAsync(ct);
        if (left.Count > 0)
        {
            output.WriteLine($"{left.Count} migration(s) still pending after migrating.");
            return 1;
        }
        output.WriteLine($"Applied {pending.Count} migration(s); database schema is up to date.");
        return 0;
    }
}
