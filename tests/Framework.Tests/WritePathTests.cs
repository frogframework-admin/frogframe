using Framework.Documents;
using Framework.Persistence;
using Framework.Persistence.PostgreSql;
using Framework.Security;
using Framework.Web;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

/// <summary>
/// Write path on PostgreSQL after the performance work: one round trip for the DocType locks and schema checks,
/// audit and outbox rows in one batch, a read cache per save operation, and a bounded connection pool. The rules must
/// stay exactly as before: stale versions and outdated schemas are refused, every audit and outbox row is stored,
/// and a save never reads back an older copy of what it wrote.
/// </summary>
sealed partial class Tests
{
    [Test]
    public Task Postgres_pool_defaults_apply_only_when_not_configured()
    {
        var cs = PostgreSqlServiceCollectionExtensions.WithPoolDefaults("Host=db;Database=x;Username=u;Password=p");
        Assert.Equal(PostgreSqlServiceCollectionExtensions.DefaultMaxPoolSize, new NpgsqlConnectionStringBuilder(cs).MaxPoolSize, "default pool size");
        foreach (var explicitPool in new[] { "Maximum Pool Size=5", "Max Pool Size=5", "MaxPoolSize=5", "maximum pool size = 5" })
        {
            var configured = $"Host=db;Database=x;{explicitPool}";
            Assert.Equal(configured, PostgreSqlServiceCollectionExtensions.WithPoolDefaults(configured), $"'{explicitPool}' is kept");
        }
        return Task.CompletedTask;
    }

    [Test]
    public async Task Postgres_write_path_keeps_its_checks_and_read_scope_never_serves_stale_writes()
    {
        var pg = Environment.GetEnvironmentVariable("FROGFRAME_TEST_PG");
        if (string.IsNullOrEmpty(pg)) return; // runs with a PostgreSQL test database
        var dbName = "ff_wpath_" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = new NpgsqlConnection(pg))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {dbName}", admin).ExecuteNonQueryAsync();
        }
        try
        {
            var conn = new NpgsqlConnectionStringBuilder(pg) { Database = dbName, Pooling = false }.ConnectionString;
            var dir = Path.Combine(Path.GetTempPath(), "frogframe-wpath-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            foreach (var name in new[] { "WpNote", "WpTag" })
                File.WriteAllText(Path.Combine(dir, $"{name}.doctype.json"), $$"""
                { "name": "{{name}}", "version": 1, "title": "{{name}}", "module": "CORE", "naming": { "pattern": "{{name[..2].ToUpperInvariant()}}-{###}" },
                  "fields": [ { "name": "text", "type": "Data", "label": "Text" } ],
                  "permissions": [ { "role": "Staff", "actions": ["read", "create", "write"], "scope": "all" } ] }
                """);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddFrogFramework(o => o.LoadFrom(dir));
            services.AddFrogFrameworkPostgreSql(conn);
            await using var sp = services.BuildServiceProvider();
            await sp.MigrateFrogFrameworkDatabaseAsync();
            _ = sp.GetRequiredService<Framework.Metadata.MetadataRegistry>(); // publishes the metadata versions
            var docs = sp.GetRequiredService<DocumentService>();
            var store = sp.GetRequiredService<IDocumentStore>();
            var u = new UserContext("ana", "Ana", "acme", new HashSet<string> { "Staff" }, new Dictionary<string, string>());

            var note = await docs.InsertAsync(u, "WpNote", new Dictionary<string, object?> { ["text"] = "one" });
            Assert.Equal(1, note.Version, "inserted through the batched commit");
            Assert.True((await docs.GetAuditAsync(u, "WpNote", note.Name)).Count >= 1, "audit stored");

            // Stale version: refused, as before.
            note = await docs.UpdateAsync(u, "WpNote", note.Name, note.Version, new Dictionary<string, object?> { ["text"] = "two" });
            var stale = await Assert.Throws<ConcurrencyException>(() => docs.UpdateAsync(u, "WpNote", note.Name, 1, new Dictionary<string, object?> { ["text"] = "x" }), "stale update");
            Assert.True(stale.Message.Contains(note.Name), stale.Message);

            // Several DocTypes, audits and outbox events in one unit of work; a repeated idempotency key is ignored.
            var now = DateTimeOffset.UtcNow;
            Document NewDoc(string docType, string name) => new()
            {
                DocType = docType, Name = name, TenantId = "acme", Owner = "ana", DocStatus = DocStatus.Draft, Created = now, Modified = now,
                ModifiedBy = "ana", Version = 1, SchemaVersion = 1, Data = new Dictionary<string, object?> { ["text"] = name },
            };
            var uow = new UnitOfWork();
            uow.Upserts.Add((NewDoc("WpTag", "WT-900"), null));
            uow.Upserts.Add((NewDoc("WpNote", "WN-900"), null));
            foreach (var n in new[] { "WT-900", "WN-900" })
                uow.Audits.Add(new AuditEntry { TenantId = "acme", DocType = n.StartsWith("WT") ? "WpTag" : "WpNote", DocName = n, Action = "Created", Actor = "Ana", At = now, SchemaVersion = 1 });
            var key = "wpath-" + Guid.NewGuid();
            for (var i = 0; i < 3; i++)
                uow.Outbox.Add(new OutboxMessage { TenantId = "acme", Type = "test.event", Payload = new() { ["i"] = i.ToString() }, IdempotencyKey = i < 2 ? key : key + "-b", CreatedAt = now });
            await store.CommitAsync(uow);
            Assert.True(await store.GetAsync("acme", "WpTag", "WT-900") is not null && await store.GetAsync("acme", "WpNote", "WN-900") is not null, "both DocTypes written");
            Assert.Equal(1, (await store.GetAuditAsync("acme", "WpTag", "WT-900")).Count, "audit of the first document");
            Assert.Equal(1, (await store.GetAuditAsync("acme", "WpNote", "WN-900")).Count, "audit of the second document");
            await using (var db = new NpgsqlConnection(conn))
            {
                await db.OpenAsync();
                var cmd = new NpgsqlCommand("SELECT count(*) FROM frogframe_outbox WHERE idempotency_key LIKE $1", db);
                cmd.Parameters.AddWithValue(key + "%");
                Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync())!, "two distinct events stored, the repeated key ignored");
            }

            // A document saved with an outdated schema version is refused, and nothing of its unit of work is stored.
            var old = NewDoc("WpNote", "WN-901");
            var oldUow = new UnitOfWork();
            oldUow.Upserts.Add((new Document
            {
                DocType = old.DocType, Name = old.Name, TenantId = old.TenantId, Owner = old.Owner, DocStatus = old.DocStatus, Created = now, Modified = now,
                ModifiedBy = "ana", Version = 1, SchemaVersion = 7, Data = old.Data,
            }, null));
            oldUow.Audits.Add(new AuditEntry { TenantId = "acme", DocType = "WpNote", DocName = "WN-901", Action = "Created", Actor = "Ana", At = now, SchemaVersion = 7 });
            var schemaError = await Assert.Throws<InvalidOperationException>(() => store.CommitAsync(oldUow), "outdated schema");
            Assert.True(schemaError.Message.Contains("versi 7") && schemaError.Message.Contains("versi 1"), schemaError.Message);
            Assert.True(await store.GetAsync("acme", "WpNote", "WN-901") is null && (await store.GetAuditAsync("acme", "WpNote", "WN-901")).Count == 0, "rolled back");

            // A DocType without published metadata is refused.
            var unknown = new UnitOfWork();
            unknown.Upserts.Add((NewDoc("NoSuchType", "X-1"), null));
            var metaError = await Assert.Throws<InvalidOperationException>(() => store.CommitAsync(unknown), "no published metadata");
            Assert.True(metaError.Message.Contains("NoSuchType"), metaError.Message);

            // Read scope: the same read once per save, a private copy per caller, never older than what the save wrote.
            async Task SetTextBehindTheStore(string text)
            {
                await using var db = new NpgsqlConnection(conn);
                await db.OpenAsync();
                var cmd = new NpgsqlCommand("UPDATE frogframe_documents SET data = jsonb_set(data, '{text}', to_jsonb($1::text)) WHERE doctype = 'WpNote' AND name = $2", db);
                cmd.Parameters.AddWithValue(text); cmd.Parameters.AddWithValue(note.Name);
                await cmd.ExecuteNonQueryAsync();
            }
            using (DocumentReadScope.Begin())
            {
                var first = (await store.GetAsync("acme", "WpNote", note.Name))!;
                first.Data["text"] = "changed by the caller";
                await SetTextBehindTheStore("changed in the database");
                var second = (await store.GetAsync("acme", "WpNote", note.Name))!;
                Assert.Equal("two", second.Data["text"], "inside a save: first read reused, as a private copy");
                second.Data["text"] = "changed by another caller";
                Assert.Equal("two", (await store.GetAsync("acme", "WpNote", note.Name))!.Data["text"], "every caller gets its own copy of the cached read");
                Assert.True(await store.GetAsync("acme", "WpNote", "WN-404") is null && await store.GetAsync("acme", "WpNote", "WN-404") is null, "not found is cached too");

                using (DocumentReadScope.Begin()) { } // a nested save joins and does not end the scope
                Assert.True(DocumentReadScope.Active, "outer scope still active");

                var update = new UnitOfWork();
                var current = (await store.GetAsync("acme", "WpNote", note.Name))!;
                var written = current.Clone();
                written.Data["text"] = "written in this save";
                written.Version = current.Version + 1;
                update.Upserts.Add((written, current.Version));
                await store.CommitAsync(update);
                Assert.Equal("written in this save", (await store.GetAsync("acme", "WpNote", note.Name))!.Data["text"], "after a commit the document is read again");
            }
            Assert.True(!DocumentReadScope.Active, "scope ended");
            await SetTextBehindTheStore("outside");
            Assert.Equal("outside", (await store.GetAsync("acme", "WpNote", note.Name))!.Data["text"], "outside a save nothing is cached");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(pg);
            await admin.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)", admin).ExecuteNonQueryAsync();
        }
    }
}
