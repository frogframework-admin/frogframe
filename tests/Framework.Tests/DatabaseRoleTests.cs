using Framework.Persistence.PostgreSql;
using Framework.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

/// <summary>Database role segregation: the application serves requests as a data-only role, the owner runs DDL.</summary>
sealed partial class Tests
{
    [Test]
    public async Task Database_app_role_can_only_read_and_write_data()
    {
        var pg = Environment.GetEnvironmentVariable("FROGFRAME_TEST_PG");
        if (string.IsNullOrEmpty(pg)) return; // runs with a PostgreSQL test database (needs CREATEDB and CREATEROLE)

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var db = "ff_roles_" + suffix;
        var role = "ff_app_" + suffix;
        await using (var admin = NpgsqlDataSource.Create(pg))
            await admin.CreateCommand($"CREATE DATABASE {db}").ExecuteNonQueryAsync();
        var owner = new NpgsqlConnectionStringBuilder(pg) { Database = db, Pooling = false }.ConnectionString;
        var app = new NpgsqlConnectionStringBuilder(pg) { Database = db, Username = role, Password = "first-Pa55word!", Pooling = false }.ConnectionString;
        try
        {
            // 1. The migrate step: migrations as the owner, then the application role and its grants.
            await using (var ownerSp = BuildIdentityProvider(s => s.AddFrogFrameworkPostgreSql(owner)))
            {
                await ownerSp.MigrateFrogFrameworkDatabaseAsync();
                await using (var ledger = NpgsqlDataSource.Create(owner))
                    await ledger.CreateCommand("CREATE TABLE frogframe_gl_entries (id bigint PRIMARY KEY, amount numeric)").ExecuteNonQueryAsync();

                await Assert.Throws<InvalidOperationException>(() => ownerSp.EnsureFrogFrameworkAppRoleAsync(role, null), "a missing role needs a password");
                await ownerSp.EnsureFrogFrameworkAppRoleAsync(role, "first-Pa55word!");
                await ownerSp.EnsureFrogFrameworkAppRoleAsync(role, "second-Pa55word!"); // idempotent; rotates the password

                await Assert.Throws<InvalidOperationException>(() => ownerSp.EnsureFrogFrameworkAppRoleAsync("postgres", "x"), "a superuser is refused");
                var ownerName = new NpgsqlConnectionStringBuilder(owner).Username!;
                await Assert.Throws<InvalidOperationException>(() => ownerSp.EnsureFrogFrameworkAppRoleAsync(ownerName, "x"), "the owner itself is refused");
                await Assert.Throws<ArgumentException>(() => ownerSp.EnsureFrogFrameworkAppRoleAsync("App; DROP TABLE x", "x"), "invalid role names are refused");
                Assert.True(!(await ownerSp.InspectFrogFrameworkRuntimeRoleAsync()).IsSegregated, "the owner connection is reported as not segregated");

                // Another superuser is refused too (and left unchanged).
                await using (var su = NpgsqlDataSource.Create(owner))
                    await su.CreateCommand($"CREATE ROLE {role}_su SUPERUSER LOGIN").ExecuteNonQueryAsync();
                await Assert.Throws<InvalidOperationException>(() => ownerSp.EnsureFrogFrameworkAppRoleAsync(role + "_su", "x"), "an existing superuser is refused");

                // A plain role that owns a table is not segregated either.
                await using (var su = NpgsqlDataSource.Create(owner))
                {
                    await su.CreateCommand($"CREATE ROLE {role}_own LOGIN NOSUPERUSER").ExecuteNonQueryAsync();
                    await su.CreateCommand($"CREATE TABLE owned_by_plain_role (i int); ALTER TABLE owned_by_plain_role OWNER TO {role}_own").ExecuteNonQueryAsync();
                }
                await using (var plainOwner = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(owner) { Username = role + "_own" }.ConnectionString))
                {
                    var plain = await AppRoleProvisioner.InspectRuntimeRoleAsync(plainOwner);
                    Assert.True(!plain.IsSuperuser && plain.OwnsSchemaTables && !plain.IsSegregated, $"table owner reported as not segregated: {plain}");
                }
                await using (var su = NpgsqlDataSource.Create(owner))
                    await su.CreateCommand("DROP TABLE owned_by_plain_role").ExecuteNonQueryAsync();

                // A table from a later migration gets the grants by default privileges.
                await using var later = NpgsqlDataSource.Create(owner);
                await later.CreateCommand("CREATE TABLE frogframe_later_feature (id serial PRIMARY KEY, note text)").ExecuteNonQueryAsync();
            }

            await using (var su = NpgsqlDataSource.Create(owner))
            {
                var attrs = await su.CreateCommand($"SELECT rolsuper, rolcreatedb, rolcreaterole, rolcanlogin, rolpassword IS NOT NULL FROM pg_authid WHERE rolname = '{role}'").ExecuteReaderAsync();
                await attrs.ReadAsync();
                Assert.True(!attrs.GetBoolean(0) && !attrs.GetBoolean(1) && !attrs.GetBoolean(2) && attrs.GetBoolean(3) && attrs.GetBoolean(4),
                    "login role with a password, no superuser/createdb/createrole");
                await attrs.DisposeAsync();
                var missing = (long)(await su.CreateCommand($"""
                    SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                    WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND c.relname <> 'frogframe_gl_entries'
                      AND NOT has_table_privilege('{role}', c.oid, 'SELECT,INSERT,UPDATE,DELETE')
                    """).ExecuteScalarAsync())!;
                Assert.Equal(0L, missing, "data access on every table, including one created after the grants");
            }

            // 2. The application: requests as the data-only role, schema work through the owner connection.
            await using var appSp = BuildIdentityProvider(s => s.AddFrogFrameworkPostgreSql(app, owner));
            var info = await appSp.InspectFrogFrameworkRuntimeRoleAsync();
            Assert.True(info.Role == role && info.IsSegregated, $"runtime role segregated: {info}");
            await appSp.WaitForFrogFrameworkMigrationsAsync(TimeSpan.FromSeconds(10));

            var (scope, _) = IdentityRequest(appSp);
            await using (scope)
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
                var created = await CreateIdentityUserAsync(users, "role-test-user");
                created.FullName = "Changed";
                Assert.True((await users.UpdateAsync(created)).Succeeded, "update as the app role");
                Assert.Equal("Changed", (await users.FindByIdAsync(created.Id))?.FullName, "read back as the app role");
                Assert.True((await users.DeleteAsync(created)).Succeeded, "delete as the app role");
            }

            await using var appDb = NpgsqlDataSource.Create(app);
            async Task<string?> Denied(string sql)
            {
                try { await appDb.CreateCommand(sql).ExecuteNonQueryAsync(); return null; }
                catch (PostgresException ex) { return ex.SqlState; }
            }
            Assert.Equal<string?>(null, await Denied("INSERT INTO frogframe_later_feature (note) VALUES ('ok')"), "insert with a sequence");
            Assert.Equal<string?>(null, await Denied("INSERT INTO frogframe_gl_entries VALUES (1, 10)"), "the ledger can be appended to");
            Assert.Equal<string?>(null, await Denied("CREATE TEMP TABLE scratch (i int)"), "temporary tables allowed");
            Assert.Equal("42501", await Denied("UPDATE frogframe_gl_entries SET amount = 0"), "ledger rows cannot be changed");
            Assert.Equal("42501", await Denied("DELETE FROM frogframe_gl_entries"), "ledger rows cannot be deleted");
            Assert.Equal("42501", await Denied("TRUNCATE frogframe_later_feature"), "no TRUNCATE");
            Assert.Equal("42501", await Denied("CREATE TABLE public.evil (i int)"), "no CREATE in schema public");
            Assert.Equal("42501", await Denied("ALTER TABLE frogframe_users ADD COLUMN evil int"), "no ALTER TABLE");
            Assert.Equal("42501", await Denied("DROP TABLE frogframe_later_feature"), "no DROP TABLE");
            Assert.Equal("42501", await Denied("ALTER TABLE frogframe_gl_entries DISABLE TRIGGER ALL"), "cannot disable triggers");
            Assert.Equal("42501", await Denied("CREATE INDEX ix_evil ON frogframe_users (user_name)"), "no CREATE INDEX");
            Assert.Equal("42501", await Denied("COPY frogframe_users TO PROGRAM 'id'"), "no COPY TO PROGRAM");
            Assert.Equal("42501", await Denied("CREATE ROLE evil"), "no CREATE ROLE");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = NpgsqlDataSource.Create(pg);
            await admin.CreateCommand($"DROP DATABASE IF EXISTS {db} WITH (FORCE)").ExecuteNonQueryAsync();
            foreach (var r in new[] { role, role + "_su", role + "_own" })
                await admin.CreateCommand($"DROP ROLE IF EXISTS {r}").ExecuteNonQueryAsync();
        }
    }
}
