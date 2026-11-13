using System.Reflection;
using Framework.Persistence;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

public interface IPostgresUnitOfWorkExtensionHandler
{
    bool CanHandle(UnitOfWorkExtension extension);
    Task ApplyAsync(IReadOnlyList<UnitOfWorkExtension> extensions, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct);
    Exception? TranslateException(PostgresException exception) => null;
}

public sealed record PostgresMigration(int Version, string Name, string Sql);

public interface IPostgresMigrationSource
{
    IEnumerable<PostgresMigration> GetMigrations();
}

public sealed class EmbeddedPostgresMigrationSource(Assembly assembly) : IPostgresMigrationSource
{
    public IEnumerable<PostgresMigration> GetMigrations()
    {
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var name = resource.Split('.')[^2];
            var version = int.Parse(name[..name.IndexOf('_')], System.Globalization.CultureInfo.InvariantCulture);
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return new(version, name, reader.ReadToEnd());
        }
    }
}
