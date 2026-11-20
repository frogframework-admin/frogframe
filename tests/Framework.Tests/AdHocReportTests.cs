using System.Text;
using Framework.Documents;
using Framework.Imports;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Persistence.PostgreSql;
using Framework.Reports;
using Framework.Security;
using Framework.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Ad-hoc report builder (P1-5): readable fields only, detail and grouped runs with aggregates, row scope,
/// strict validation (no filtering on hidden fields), saved reports shared with roles that run with the viewer's
/// permissions, ownership and concurrency, export permission and content, and (with FROGFRAME_TEST_PG) the
/// PostgreSQL store.
/// </summary>
sealed partial class Tests
{
    private static ServiceProvider BuildAdHocProvider(Action<IServiceCollection>? extra = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "frogframe-adhoc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SalesOrder.doctype.json"), """
        {
          "name": "SalesOrder", "version": 1, "title": "Sales Order", "module": "CORE",
          "naming": { "pattern": "SO-{#####}" },
          "listView": { "titleField": "customer", "searchFields": ["name", "customer"] },
          "fields": [
            { "name": "customer", "type": "Data", "label": "Customer", "requiredWhen": "true", "inListView": true },
            { "name": "region", "type": "Select", "label": "Region", "options": ["North", "South"], "inListView": true },
            { "name": "amount", "type": "Currency", "label": "Amount", "inListView": true },
            { "name": "qty", "type": "Int", "label": "Qty" },
            { "name": "order_date", "type": "Date", "label": "Order date" },
            { "name": "is_rush", "type": "Check", "label": "Rush" },
            { "name": "margin", "type": "Currency", "label": "Margin", "permLevel": 1 }
          ],
          "permissions": [
            { "role": "Sales", "actions": ["read", "create", "report"], "scope": "owner" },
            { "role": "Manager", "actions": ["read", "create", "report", "export"], "scope": "all" },
            { "role": "Manager", "actions": ["read"], "permLevel": 1, "scope": "all" },
            { "role": "Analyst", "actions": ["read", "report"], "scope": "all" }
          ]
        }
        """);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        extra?.Invoke(services);
        services.AddFrogFramework(o => o.LoadFrom(dir));
        return services.BuildServiceProvider();
    }

    private static UserContext AdHocUser(string id, string tenant, params string[] roles) =>
        new(id, id, tenant, new HashSet<string>(roles), new Dictionary<string, string>());

    [Test]
    public async Task AdHoc_report_runs_detail_and_grouped_with_permissions_and_saved_sharing()
    {
        await using var provider = BuildAdHocProvider();
        var docs = provider.GetRequiredService<DocumentService>();
        var reports = provider.GetRequiredService<AdHocReportService>();
        UserContext mgr = AdHocUser("mia", "acme", "Manager"), ana = AdHocUser("ana", "acme", "Sales"), bob = AdHocUser("bob", "acme", "Sales"),
            al = AdHocUser("al", "acme", "Analyst");
        var sysAdmin = AdHocUser("root", "acme", UserContext.SystemManager);
        async Task Add(UserContext u, string customer, string region, decimal amount, int qty, string date, bool rush, decimal margin)
        {
            // Sales cannot write the permission-level-1 margin: the owner creates the order, an administrator sets the margin.
            var d = await docs.InsertAsync(u, "SalesOrder", new Dictionary<string, object?>
                { ["customer"] = customer, ["region"] = region, ["amount"] = amount, ["qty"] = qty, ["order_date"] = date, ["is_rush"] = rush });
            await docs.UpdateAsync(sysAdmin, "SalesOrder", d.Name, d.Version, new Dictionary<string, object?> { ["margin"] = margin });
        }
        await Add(ana, "Acme", "North", 100, 1, "2026-11-01", false, 10);
        await Add(ana, "Bolt", "North", 300, 3, "2026-11-05", true, 30);
        await Add(ana, "=HYPERLINK(1)", "South", 50, 5, "2026-11-03", false, 5);
        await Add(bob, "Core", "South", 200, 2, "2026-11-02", true, 20);
        await Add(bob, "Dune", "North", 400, 4, "2026-11-09", false, 40);

        Assert.True(reports.DocTypes(ana).Any(m => m.Name == "SalesOrder"), "report permission lists the DocType");
        Assert.True(reports.Fields(ana, "SalesOrder").All(f => f.Key != "margin"), "permlevel-1 field hidden from Sales");
        Assert.True(reports.Fields(mgr, "SalesOrder").Any(f => f.Key == "margin"), "manager sees margin");

        // Detail: filter, sort, limit.
        var detail = await reports.RunAsync(mgr, new AdHocReportDef
        {
            DocType = "SalesOrder", Columns = ["name", "customer", "amount", "margin"],
            Filters = [new("amount", ">=", "100"), new("region", "in", "North, South")],
            SortBy = "amount", SortDescending = true, Limit = 3,
        });
        Assert.True(detail.Columns.Select(c => c.Key).SequenceEqual(["name", "customer", "amount", "margin"]), "columns");
        Assert.True(detail.Rows.Select(r => r[1]).SequenceEqual(new object?[] { "Dune", "Bolt", "Core" }), "sorted desc, limited: " + string.Join(",", detail.Rows.Select(r => r[1])));
        Assert.True(detail.LimitApplied && detail.SourceRows == 4 && !detail.Truncated, "limit flag and source count");

        // Grouped with aggregates (manager: all rows).
        var grouped = await reports.RunAsync(mgr, new AdHocReportDef
        {
            DocType = "SalesOrder", GroupBy = ["region"],
            Aggregates = [new("count", null), new("sum", "amount"), new("avg", "qty"), new("min", "order_date"), new("max", "customer")],
        });
        Assert.True(grouped.Columns.Select(c => c.Key).SequenceEqual(["region", "count", "sum:amount", "avg:qty", "min:order_date", "max:customer"]), "grouped columns");
        object?[] G(string region) => grouped.Rows.Single(r => (string?)r[0] == region);
        Assert.True(G("North").Skip(1).SequenceEqual(new object?[] { 3m, 800m, 8m / 3m is var avg ? Math.Round(avg, 6) : 0m, "2026-11-01", "Dune" }), "North: " + string.Join("|", G("North")));
        Assert.True(G("South").Skip(1).Take(3).SequenceEqual(new object?[] { 2m, 250m, 3.5m }), "South: " + string.Join("|", G("South")));
        Assert.Equal("Currency", grouped.Columns[2].Type, "sum keeps the currency type");

        // Row scope: Sales only aggregates their own documents.
        var anaGrouped = await reports.RunAsync(ana, new AdHocReportDef { DocType = "SalesOrder", GroupBy = ["region"], Aggregates = [new("sum", "amount")] });
        Assert.Equal(400m, anaGrouped.Rows.Single(r => (string?)r[0] == "North")[1], "ana's North total only");
        var rush = await reports.RunAsync(mgr, new AdHocReportDef { DocType = "SalesOrder", Columns = ["customer"], Filters = [new("is_rush", "=", "yes")], SortBy = "customer" });
        Assert.True(rush.Rows.Select(r => r[0]).SequenceEqual(new object?[] { "Bolt", "Core" }), "check filter parses yes/no");

        // Strict validation: hidden fields cannot be shown, filtered or summarised; types are checked.
        async Task Rejects(UserContext u, AdHocReportDef d, string why) =>
            await Assert.Throws<ValidationFailedException>(() => reports.RunAsync(u, d), why);
        await Rejects(ana, new() { DocType = "SalesOrder", Columns = ["customer", "margin"] }, "hidden column");
        await Rejects(ana, new() { DocType = "SalesOrder", Columns = ["customer"], Filters = [new("margin", ">", "15")] }, "filter on a hidden field");
        await Rejects(ana, new() { DocType = "SalesOrder", GroupBy = ["region"], Aggregates = [new("sum", "margin")] }, "sum of a hidden field");
        await Rejects(mgr, new() { DocType = "SalesOrder", GroupBy = ["region"], Aggregates = [new("sum", "customer")] }, "sum of text");
        await Rejects(mgr, new() { DocType = "SalesOrder", Columns = ["customer"], Filters = [new("amount", ">", "abc")] }, "not a number");
        await Rejects(mgr, new() { DocType = "SalesOrder", Columns = ["customer"], Filters = [new("amount", "~", "1")] }, "unknown operator");
        await Rejects(mgr, new() { DocType = "SalesOrder", Columns = [] }, "nothing to show");
        await Assert.Throws<PermissionDeniedException>(() => reports.RunAsync(AdHocUser("x", "acme", "Other"), new() { DocType = "SalesOrder", Columns = ["name"] }), "no report permission");

        // Saved and shared with Sales: runs with the viewer's own permissions.
        var saved = await reports.SaveAsync(mgr, null, "Orders by region", null, ["Manager"], new AdHocReportDef
        {
            DocType = "SalesOrder", GroupBy = ["region"], Aggregates = [new("sum", "amount"), new("sum", "margin")],
        }, null);
        Assert.True((await reports.ListAsync(ana)).All(r => r.Id != saved.Id), "not shared with Sales yet");
        await Assert.Throws<PermissionDeniedException>(() => reports.SaveAsync(mgr, saved.Id, saved.Name, null, ["Sales"], saved.Definition, saved.Version),
            "cannot share with a role you do not have");
        var admin = sysAdmin;
        saved = await reports.SaveAsync(admin, saved.Id, saved.Name, "Totals per region", ["Sales", "Manager"], saved.Definition, saved.Version);
        Assert.Equal(2, saved.Version, "version increments");
        Assert.True((await reports.ListAsync(ana)).Any(r => r.Id == saved.Id), "shared with Sales");
        var (_, asAna) = await reports.RunSavedAsync(ana, saved.Id);
        Assert.True(asAna.SkippedFields.SequenceEqual(["margin"]) && asAna.Columns.All(c => c.Key != "sum:margin"), "margin left out for Sales");
        Assert.Equal(400m, asAna.Rows.Single(r => (string?)r[0] == "North")[1], "and only ana's rows");
        Assert.True(!AdHocReportService.CanEdit(ana, saved), "shared viewer cannot edit");
        await Assert.Throws<PermissionDeniedException>(() => reports.SaveAsync(ana, saved.Id, "Mine now", null, [], saved.Definition, saved.Version), "not the owner");
        await Assert.Throws<PermissionDeniedException>(() => reports.DeleteAsync(ana, saved.Id), "cannot delete");
        await Assert.Throws<ConcurrencyException>(() => reports.SaveAsync(mgr, saved.Id, saved.Name, null, ["Manager"], saved.Definition, 1), "stale version");
        var copy = await reports.SaveAsync(ana, null, "My copy", null, [], new AdHocReportDef { DocType = "SalesOrder", Columns = ["customer"] }, null);
        Assert.True((await reports.ListAsync(bob)).All(r => r.Id != copy.Id), "a private report is the owner's only");

        // A saved filter on a field the viewer cannot read is refused, never silently dropped.
        var filtered = await reports.SaveAsync(mgr, null, "High margin", null, ["Manager"], new AdHocReportDef
        {
            DocType = "SalesOrder", Columns = ["customer"], Filters = [new("margin", ">", "25")],
        }, null);
        filtered = await reports.SaveAsync(admin, filtered.Id, filtered.Name, null, ["Manager", "Sales"], filtered.Definition, filtered.Version);
        await Assert.Throws<PermissionDeniedException>(() => reports.RunSavedAsync(ana, filtered.Id), "hidden filter refused for Sales");
        Assert.Equal(2, (await reports.RunSavedAsync(mgr, filtered.Id)).Result.Rows.Count, "manager runs it");

        // Export: permission and content.
        var def = new AdHocReportDef { DocType = "SalesOrder", Columns = ["customer", "amount", "is_rush"], SortBy = "customer" };
        await Assert.Throws<PermissionDeniedException>(() => reports.ExportAsync(al, def, "xlsx", "x"), "analyst may report but not export");
        var xlsx = await reports.ExportAsync(mgr, def, "xlsx", "Orders / Nov");
        Assert.Equal("Orders---Nov.xlsx", xlsx.FileName, "safe file name");
        var sheet = SpreadsheetReader.Read(xlsx.Content, xlsx.FileName, 10_000_000)[0];
        Assert.True(sheet.Rows[0].Select(v => v?.ToString()).SequenceEqual(["Customer", "Amount", "Rush"]), "headers");
        Assert.Equal(6, sheet.Rows.Count, "header + 5 rows");
        var csv = await reports.ExportAsync(mgr, def, "csv", "orders");
        var text = Encoding.UTF8.GetString(csv.Content, 3, csv.Content.Length - 3);
        Assert.True(text.Contains("\"'=HYPERLINK(1)\",50,No"), "formula guard and yes/no: " + text);
    }

    [Test]
    public async Task AdHoc_postgres_store_round_trip()
    {
        var pg = Environment.GetEnvironmentVariable("FROGFRAME_TEST_PG");
        if (string.IsNullOrEmpty(pg)) return; // runs with a PostgreSQL test database
        await using var provider = BuildAdHocProvider(s => s.AddFrogFrameworkPostgreSql(pg));
        await provider.MigrateFrogFrameworkDatabaseAsync();
        var store = provider.GetRequiredService<IAdHocReportStore>();
        Assert.True(store is PostgresAdHocReportStore, "PostgreSQL report store is registered");
        var reports = provider.GetRequiredService<AdHocReportService>();
        var tenant = "adhoc-" + Guid.NewGuid().ToString("N")[..8];
        var mgr = AdHocUser("mia", tenant, "Manager");
        var def = new AdHocReportDef
        {
            DocType = "SalesOrder", GroupBy = ["region"], Aggregates = [new("sum", "amount"), new("count", null)],
            Filters = [new("region", "in", "North,South")], SortBy = "sum:amount", SortDescending = true, Limit = 50,
        };
        var saved = await reports.SaveAsync(mgr, null, "Region totals", "PG", ["Manager"], def, null);
        var back = await reports.GetAsync(mgr, saved.Id);
        Assert.Equal("Region totals", back.Name, "name");
        Assert.True(back.SharedWithRoles.SequenceEqual(["Manager"]), "roles array");
        Assert.True(back.Definition.GroupBy.SequenceEqual(["region"]) && back.Definition.Aggregates.Count == 2
                    && back.Definition.Aggregates[1] == new AdHocAggregate("count", null) && back.Definition.Filters[0] == new AdHocFilter("region", "in", "North,South")
                    && back.Definition.SortBy == "sum:amount" && back.Definition.SortDescending && back.Definition.Limit == 50, "definition JSON round trip");
        var updated = await reports.SaveAsync(mgr, saved.Id, "Region totals v2", null, [], def, saved.Version);
        Assert.Equal(2, (await reports.GetAsync(mgr, saved.Id)).Version, "update with version check");
        await Assert.Throws<ConcurrencyException>(() => reports.SaveAsync(mgr, saved.Id, "x", null, [], def, 1), "stale version in SQL");
        Assert.True((await reports.ListAsync(mgr)).Any(r => r.Id == saved.Id && r.Name == "Region totals v2" && r.Description is null), "list");
        await reports.DeleteAsync(mgr, updated.Id);
        Assert.True((await store.ListAsync(tenant)).Count == 0, "deleted");
    }
}
