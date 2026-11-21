using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Persistence.PostgreSql;
using Framework.Security;
using Framework.Web;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

/// <summary>
/// P2-5: indexes declared in DocType metadata (several fields together, or a prefix index) and the "startswith"
/// filter. On PostgreSQL the queries the HR/WSM hooks run (employee + work date, units under a path) must use them.
/// </summary>
sealed partial class Tests
{
    private static string WriteIndexMetadata()
    {
        var dir = Path.Combine(Path.GetTempPath(), "frogframe-ix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "IxUnit.doctype.json"), """
        { "name": "IxUnit", "version": 1, "title": "Unit", "module": "CORE", "naming": { "pattern": "U-{#####}" },
          "fields": [ { "name": "path", "type": "Data", "label": "Path" }, { "name": "title", "type": "Data", "label": "Title" } ],
          "indexes": [ { "fields": ["path"], "prefix": true } ],
          "listView": { "columns": ["path"], "filters": ["path"] },
          "permissions": [ { "role": "HR", "actions": ["read", "create"], "scope": "all" } ] }
        """);
        File.WriteAllText(Path.Combine(dir, "IxSchedule.doctype.json"), """
        { "name": "IxSchedule", "version": 1, "title": "Schedule", "module": "CORE", "naming": { "pattern": "S-{#####}" },
          "fields": [ { "name": "employee", "type": "Data", "label": "Employee" }, { "name": "work_date", "type": "Date", "label": "Work date" },
                      { "name": "hours", "type": "Float", "label": "Hours" } ],
          "indexes": [ { "fields": ["employee", "work_date"] } ],
          "permissions": [ { "role": "HR", "actions": ["read", "create"], "scope": "all" } ] }
        """);
        return dir;
    }

    [Test]
    public async Task Declared_indexes_are_validated_generated_and_startswith_filters_by_prefix()
    {
        var dir = WriteIndexMetadata();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFrogFramework(o => o.LoadFrom(dir));
        await using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<MetadataRegistry>();

        // Generated DDL: tenant first, then the fields; the prefix index uses text_pattern_ops on its last field.
        var specs = DocumentIndexSync.Desired(registry.Current);
        var composite = specs.Single(s => s.DocType == "IxSchedule" && s.Kind == "composite");
        Assert.Equal("ON frogframe_documents (tenant_id, ((data ->> 'employee')), ((data ->> 'work_date'))) WHERE doctype = 'IxSchedule'", composite.Ddl, "composite DDL");
        var prefix = specs.Single(s => s.DocType == "IxUnit" && s.Kind == "prefix");
        Assert.Equal("ON frogframe_documents (tenant_id, ((data ->> 'path')) text_pattern_ops) WHERE doctype = 'IxUnit'", prefix.Ddl, "prefix DDL");
        Assert.Equal(specs.Count, specs.Select(s => s.Ddl).Distinct().Count(), "no duplicate index definitions");

        DocTypeDef With(string docType, params IndexDef[] indexes)
        {
            var def = MetadataJson.ParseDocType(MetadataJson.Serialize(registry.Current.Get(docType).Def));
            def.Indexes = [.. indexes];
            return def;
        }
        // Validation of declared indexes (compiled from DocType files; the UI cannot change indexes of app DocTypes).
        var compiler = sp.GetRequiredService<MetadataCompiler>();
        IReadOnlyList<string> Errors(params IndexDef[] indexes)
        {
            var defs = registry.Current.DocTypes.Values.Select(m => MetadataJson.ParseDocType(MetadataJson.Serialize(m.Def))).ToList();
            defs.Single(d => d.Name == "IxSchedule").Indexes = [.. indexes];
            try { compiler.Compile(defs, []); return []; }
            catch (MetadataCompilationException ex) { return ex.Errors.Where(e => e.Path.StartsWith("indexes", StringComparison.Ordinal)).Select(e => e.Message).ToList(); }
        }
        Assert.Equal(0, Errors(new IndexDef { Fields = ["hours", "employee"] }).Count, "valid composite");
        Assert.True(Errors(new IndexDef { Fields = ["nope"] }).Single().Contains("'nope' cannot be indexed"), "unknown field");
        Assert.True(Errors(new IndexDef { Fields = [] }).Single().Contains("1 to 4 fields"), "no fields");
        Assert.True(Errors(new IndexDef { Fields = ["employee", "employee"] }).Single().Contains("more than once"), "repeated field");
        Assert.True(Errors(new IndexDef { Fields = ["employee", "hours"], Prefix = true }).Single().Contains("must be a text field"), "prefix on a number");
        Assert.True(Errors(new IndexDef { Fields = ["employee", "work_date", "hours", "employee2", "x"] }).Any(e => e.Contains("1 to 4 fields")), "too many fields");
        Assert.True(registry.Preview(With("IxSchedule", new IndexDef { Fields = ["hours"] })).Errors.Any(e => e.Message.Contains("only be changed by the developers")),
            "indexes of app DocTypes are not editable from the UI");

        // "startswith": case-sensitive prefix, through the API (in-memory store).
        var docs = sp.GetRequiredService<DocumentService>();
        var hr = new UserContext("hana", "Hana", "acme", new HashSet<string> { "HR" }, new Dictionary<string, string>());
        foreach (var p in new[] { "/HQ/", "/HQ/FIN/", "/HQ/FIN/AP/", "/HQ/FINANCE-2/", "/HQX/", "/hq/fin/", "/OPS/HQ/FIN/" })
            await docs.InsertAsync(hr, "IxUnit", new Dictionary<string, object?> { ["path"] = p });
        async Task<string> Under(string op, string value) => string.Join(" ", (await docs.ListAsync(hr, "IxUnit",
                new ListRequest([new FilterClause("path", op, value)], SortField: "path", SortDirection: "asc", PageSize: 50)))
            .Items.Select(i => (string)i["path"]!).Order(StringComparer.Ordinal));
        Assert.Equal("/HQ/FIN/ /HQ/FIN/AP/", await Under("startswith", "/HQ/FIN/"), "prefix only, case-sensitive");
        Assert.Equal("/HQ/FIN/ /HQ/FIN/AP/ /OPS/HQ/FIN/ /hq/fin/", await Under("like", "/HQ/FIN/"), "'like' stays contains, case-insensitive");
        Assert.True(FilterOps.All.Contains("startswith"), "operator allowed");
    }

    [Test]
    public async Task Declared_indexes_are_used_by_postgresql_for_hook_queries()
    {
        var pg = Environment.GetEnvironmentVariable("FROGFRAME_TEST_PG");
        if (string.IsNullOrEmpty(pg)) return; // runs with a PostgreSQL test database

        var dbName = "ff_ixtest_" + Guid.NewGuid().ToString("N")[..8];
        await using (var admin = new NpgsqlConnection(pg))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {dbName}", admin).ExecuteNonQueryAsync();
        }
        try
        {
            var conn = new NpgsqlConnectionStringBuilder(pg) { Database = dbName, Pooling = false }.ConnectionString;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddFrogFramework(o => o.LoadFrom(WriteIndexMetadata()));
            services.AddFrogFrameworkPostgreSql(conn);
            await using var sp = services.BuildServiceProvider();
            await sp.MigrateFrogFrameworkDatabaseAsync();
            var docs = sp.GetRequiredService<DocumentService>();
            var hr = new UserContext("hana", "Hana", "acme", new HashSet<string> { "HR" }, new Dictionary<string, string>());
            await docs.InsertAsync(hr, "IxUnit", new Dictionary<string, object?> { ["path"] = "/HQ/FIN/", ["title"] = "Finance" });
            await docs.InsertAsync(hr, "IxSchedule", new Dictionary<string, object?> { ["employee"] = "E-1", ["work_date"] = "2026-01-05", ["hours"] = 8m });

            // Realistic volume: 30,000 units in 300 branches, 60,000 schedule rows for 600 employees.
            await using (var db = new NpgsqlConnection(conn))
            {
                await db.OpenAsync();
                const string cols = "tenant_id, doctype, name, owner, docstatus, workflow_state, created, modified, modified_by, version, schema_version, workflow_version, amended_from";
                const string copy = "tenant_id, doctype, name || '-' || g, owner, docstatus, workflow_state, created, modified, modified_by, version, schema_version, workflow_version, amended_from";
                await new NpgsqlCommand($$"""
                    INSERT INTO frogframe_documents ({{cols}}, data)
                    SELECT {{copy}}, jsonb_set(data, '{path}', to_jsonb('/B' || (g % 300) || '/U' || g || '/'))
                    FROM frogframe_documents, generate_series(1, 30000) g WHERE doctype = 'IxUnit';
                    INSERT INTO frogframe_documents ({{cols}}, data)
                    SELECT {{copy}}, jsonb_set(jsonb_set(data, '{employee}', to_jsonb('E-' || (g % 600))), '{work_date}',
                        to_jsonb(to_char(date '2026-01-01' + (g / 600), 'YYYY-MM-DD')))
                    FROM frogframe_documents, generate_series(1, 60000) g WHERE doctype = 'IxSchedule';
                    ANALYZE frogframe_documents;
                    """, db).ExecuteNonQueryAsync();
            }
            await sp.SyncDocumentIndexesAsync();

            // Count index scans before and after running the queries the hooks run (each on its own connection,
            // whose statistics are flushed when it closes).
            async Task<long> Scans(string kindLetter, string docType)
            {
                await using var db = new NpgsqlConnection(conn);
                await db.OpenAsync();
                await new NpgsqlCommand("SELECT pg_stat_clear_snapshot()", db).ExecuteNonQueryAsync();
                await using var cmd = new NpgsqlCommand(
                    "SELECT coalesce(sum(idx_scan), 0) FROM pg_stat_user_indexes WHERE indexrelname LIKE $1", db);
                cmd.Parameters.AddWithValue($"ix\\_ff\\_{(docType == "IxUnit" ? "ix_unit" : "ix_schedule")}\\_%\\_{kindLetter}%");
                return (long)(decimal)(await cmd.ExecuteScalarAsync())!;
            }
            var store = sp.GetRequiredService<IDocumentStore>();
            var prefixBefore = await Scans("p", "IxUnit");
            var compositeBefore = await Scans("c", "IxSchedule");

            var under = await store.QueryAsync(new StoreQuery("acme", "IxUnit", RowScope.All,
                [new FilterClause("path", "startswith", "/B7/")], null, [], [], 0, 5000));
            Assert.Equal(100, under.Items.Count, "units under /B7/");
            Assert.True(under.Items.All(d => ((string)d.Data["path"]!).StartsWith("/B7/", StringComparison.Ordinal)), "prefix results");
            var day = await store.QueryAsync(new StoreQuery("acme", "IxSchedule", RowScope.All,
                [new FilterClause("employee", "=", "E-42"), new FilterClause("work_date", "=", "2026-01-31")], null, [], [], 0, 1));
            Assert.Equal(1, day.Items.Count, "one schedule per employee and day");
            var window = await store.QueryAsync(new StoreQuery("acme", "IxSchedule", RowScope.All,
                [new FilterClause("employee", "=", "E-42"), new FilterClause("work_date", ">=", "2026-01-10"), new FilterClause("work_date", "<=", "2026-01-20")],
                null, [], [new SortDef { Field = "work_date", Direction = "asc" }], 0, 500));
            Assert.Equal(11, window.Items.Count, "eleven days in the window");

            await Task.Delay(700); // statistics of the closed connections reach the cumulative stats
            var prefixUsed = await Scans("p", "IxUnit") - prefixBefore;
            var compositeUsed = await Scans("c", "IxSchedule") - compositeBefore;
            Assert.True(prefixUsed > 0, $"startswith uses the prefix index (scans: {prefixUsed})");
            Assert.True(compositeUsed >= 2, $"employee + work date queries use the composite index (scans: {compositeUsed})");
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
