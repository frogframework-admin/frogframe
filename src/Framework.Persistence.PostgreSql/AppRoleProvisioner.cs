using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Framework.Persistence.PostgreSql;

/// <summary>How the runtime connection (ConnectionStrings:FrogFrame) is privileged; see <see cref="AppRoleProvisioner.InspectRuntimeRoleAsync"/>.</summary>
public sealed record RuntimeRoleInfo(string Role, bool IsSuperuser, bool OwnsSchemaTables, bool CanCreateInSchema)
{
    /// <summary>True when the runtime role is a plain data role (not superuser, not table owner, no CREATE on public).</summary>
    public bool IsSegregated => !IsSuperuser && !OwnsSchemaTables && !CanCreateInSchema;
}

/// <summary>
/// Database role segregation. The schema owner (ConnectionStrings:FrogFrameSchema) runs migrations and other DDL; the
/// application serves requests as a separate runtime role (ConnectionStrings:FrogFrame) that can only read and write
/// data: it cannot create, alter or drop objects, cannot disable triggers, and has no UPDATE/DELETE on the general
/// ledger. <see cref="EnsureAsync"/> creates or updates that role and its grants; it is idempotent and runs after every
/// migration, so new tables get the same grants. See docs/guides/database-roles.md.
/// </summary>
public static partial class AppRoleProvisioner
{
    /// <summary>Tables the runtime role may read and append to, but never change or delete (append-only).</summary>
    public static readonly string[] AppendOnlyTables = ["frogframe_gl_entries"];

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex RoleName();

    /// <summary>
    /// Creates <paramref name="role"/> if missing (LOGIN, no superuser, no CREATEDB/CREATEROLE), sets its password
    /// when one is given, and grants data access on every table and sequence in schema public, now and for tables the
    /// owner creates later. Must run as the schema owner (with CREATEROLE, or a superuser, the first time).
    /// </summary>
    public static async Task EnsureAsync(NpgsqlDataSource ownerSource, string role, string? password, ILogger? log = null, CancellationToken ct = default)
    {
        if (!RoleName().IsMatch(role))
            throw new ArgumentException($"Database:AppRole '{role}' is not a valid role name (lower-case letters, digits and _).", nameof(role));

        await using var conn = await ownerSource.OpenConnectionAsync(ct);
        var owner = (string)(await Scalar(conn, "SELECT current_user", ct))!;
        if (string.Equals(owner, role, StringComparison.Ordinal))
            throw new InvalidOperationException($"Database:AppRole '{role}' is the schema owner itself. Use a separate role for the application.");

        var existing = await ReadRoleAsync(conn, role, ct);
        if (existing is { Super: true })
            throw new InvalidOperationException($"Database role '{role}' is a superuser and cannot be used as the restricted application role.");

        await using var tx = await conn.BeginTransactionAsync(ct);
        if (existing is null)
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException($"Database role '{role}' does not exist. Set Database:AppRolePassword (FROGFRAME_APP_PASSWORD) so it can be created.");
            await ExecFormatted(conn, tx, "CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS PASSWORD %L", role, password, ct);
            log?.LogInformation("Created database role {Role} for the application", role);
        }
        else
        {
            // Keep it a plain login role, and follow password changes in the configuration.
            await ExecFormatted(conn, tx, "ALTER ROLE %I LOGIN NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS", role, null, ct);
            if (!string.IsNullOrEmpty(password))
                await ExecFormatted(conn, tx, "ALTER ROLE %I PASSWORD %L", role, password, ct);
        }

        var statements = new List<string>
        {
            "GRANT CONNECT, TEMPORARY ON DATABASE {db} TO {role}",
            "GRANT USAGE ON SCHEMA public TO {role}",
            // No one but the owner creates objects in public (already the default from PostgreSQL 15).
            "REVOKE CREATE ON SCHEMA public FROM PUBLIC",
            "REVOKE CREATE ON SCHEMA public FROM {role}",
            "GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {role}",
            "GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO {role}",
            "ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {role}",
            "ALTER DEFAULT PRIVILEGES FOR ROLE {owner} IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {role}",
        };
        foreach (var sql in statements)
            await ExecIdents(conn, tx, sql, role, owner, ct);

        foreach (var table in AppendOnlyTables)
            if (await Scalar(conn, $"SELECT to_regclass('public.{table}') IS NOT NULL", ct, tx) is true)
                await ExecIdents(conn, tx, $"REVOKE UPDATE, DELETE, TRUNCATE ON public.{table} FROM {{role}}", role, owner, ct);

        await tx.CommitAsync(ct);
        log?.LogInformation("Database role {Role}: data access granted on schema public (owner {Owner})", role, owner);
    }

    /// <summary>Privileges of the role the runtime connection uses, for the startup warning and the health page.</summary>
    public static async Task<RuntimeRoleInfo> InspectRuntimeRoleAsync(NpgsqlDataSource runtime, CancellationToken ct = default)
    {
        await using var conn = await runtime.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand("""
            SELECT current_user,
                   (SELECT rolsuper FROM pg_roles WHERE rolname = current_user),
                   EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tableowner = current_user),
                   has_schema_privilege(current_user, 'public', 'CREATE')
            """, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new RuntimeRoleInfo(r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3));
    }

    private static async Task<(bool Super, bool Login)?> ReadRoleAsync(NpgsqlConnection conn, string role, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT rolsuper, rolcanlogin FROM pg_roles WHERE rolname = $1", conn);
        cmd.Parameters.AddWithValue(role);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetBoolean(0), r.GetBoolean(1)) : null;
    }

    private static async Task<object?> Scalar(NpgsqlConnection conn, string sql, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        return await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>Builds a statement with format() on the server (%I identifier, %L literal), then runs it.</summary>
    private static async Task ExecFormatted(NpgsqlConnection conn, NpgsqlTransaction tx, string template, string ident, string? literal, CancellationToken ct)
    {
        await using var build = new NpgsqlCommand(literal is null ? "SELECT format($1, $2)" : "SELECT format($1, $2, $3)", conn, tx);
        build.Parameters.AddWithValue(template);
        build.Parameters.AddWithValue(ident);
        if (literal is not null) build.Parameters.AddWithValue(literal);
        var sql = (string)(await build.ExecuteScalarAsync(ct))!;
        await using var run = new NpgsqlCommand(sql, conn, tx);
        await run.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Replaces {role}, {owner} and {db} with quoted identifiers (quote_ident on the server).</summary>
    private static async Task ExecIdents(NpgsqlConnection conn, NpgsqlTransaction tx, string template, string role, string owner, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand("SELECT quote_ident($1), quote_ident($2), quote_ident(current_database())", conn, tx);
        q.Parameters.AddWithValue(role);
        q.Parameters.AddWithValue(owner);
        string r1, o1, d1;
        await using (var rd = await q.ExecuteReaderAsync(ct))
        {
            await rd.ReadAsync(ct);
            (r1, o1, d1) = (rd.GetString(0), rd.GetString(1), rd.GetString(2));
        }
        await using var cmd = new NpgsqlCommand(template.Replace("{role}", r1).Replace("{owner}", o1).Replace("{db}", d1), conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public static class AppRoleServiceCollectionExtensions
{
    /// <summary>
    /// Database:AppRole (and Database:AppRolePassword): ensures the restricted runtime role with the schema-owner
    /// connection. No-op when Database:AppRole is empty.
    /// </summary>
    public static Task EnsureFrogFrameworkAppRoleAsync(this IServiceProvider services, string? role, string? password, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(role) ? Task.CompletedTask
            : AppRoleProvisioner.EnsureAsync(services.GetRequiredService<SchemaDataSource>().Source, role.Trim(), password,
                services.GetService<ILoggerFactory>()?.CreateLogger("Framework.Persistence.PostgreSql.AppRole"), ct);

    /// <summary>Privileges of the runtime connection (ConnectionStrings:FrogFrame).</summary>
    public static Task<RuntimeRoleInfo> InspectFrogFrameworkRuntimeRoleAsync(this IServiceProvider services, CancellationToken ct = default) =>
        AppRoleProvisioner.InspectRuntimeRoleAsync(services.GetRequiredService<NpgsqlDataSource>(), ct);
}
