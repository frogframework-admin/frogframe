using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Framework.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// List tree view (P1-3): parent field detection on the ERP masters, forest building from list rows (order, depth,
/// descendant counts), rows whose parent is filtered out, parent cycles in bad data, expansion helpers and
/// metadata validation.
/// </summary>
sealed partial class Tests
{
    private static ServiceProvider BuildTreeProvider(string treeJson)
    {
        var dir = Path.Combine(Path.GetTempPath(), "frogframe-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Ledger.doctype.json"), $$"""
        {
          "name": "Ledger", "version": 1, "title": "Ledger", "module": "CORE",
          "naming": { "pattern": "field:code" },
          "listView": { "titleField": "ledger_name", "searchFields": ["code", "ledger_name"],
                        "defaultSort": [ { "field": "code", "direction": "asc" } ] {{treeJson}} },
          "fields": [
            { "name": "code", "type": "Data", "label": "Code", "requiredWhen": "true", "inListView": true },
            { "name": "ledger_name", "type": "Data", "label": "Name", "requiredWhen": "true", "inListView": true },
            { "name": "previous_version", "type": "Link", "linkTo": "Ledger", "label": "Previous version" },
            { "name": "parent_ledger", "type": "Link", "linkTo": "Ledger", "label": "Parent", "inListView": true },
            { "name": "is_group", "type": "Check", "label": "Is group", "inListView": true },
            { "name": "root_type", "type": "Select", "label": "Root type", "options": ["Asset", "Liability"], "inListView": true }
          ],
          "permissions": [ { "role": "Staff", "actions": ["read", "create", "write"], "scope": "all" } ]
        }
        """);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddFrogFramework(o => o.LoadFrom(dir));
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task Tree_detects_parent_fields_and_builds_the_forest_from_list_rows()
    {
        // The ERP masters named in the backlog get a tree without configuration.
        await using (var erp = BuildErpProvider())
        {
            var snap = erp.GetRequiredService<IMetadataProvider>().Current;
            Assert.Equal(new TreeView.Settings("parent_account", "is_group"), TreeView.Resolve(snap.Get("Account")), "chart of accounts");
            Assert.Equal(new TreeView.Settings("parent_cost_center", "is_group"), TreeView.Resolve(snap.Get("CostCenter")), "cost centers");
            Assert.Equal("parent_org_unit", TreeView.Resolve(snap.Get("OrganizationUnit"))?.ParentField, "organization units: parent link, not previous_version");
        }

        await using var provider = BuildTreeProvider("");
        var meta = provider.GetRequiredService<IMetadataProvider>().Current.Get("Ledger");
        var docs = provider.GetRequiredService<DocumentService>();
        var staff = new UserContext("ana", "Ana", "acme", new HashSet<string> { "Staff" }, new Dictionary<string, string>());
        var settings = TreeView.Resolve(meta)!;
        Assert.Equal(new TreeView.Settings("parent_ledger", "is_group"), settings, "auto: the 'parent…' link wins over another self link");

        async Task Add(string code, string name, string? parent, bool group, string root = "Asset") =>
            await docs.InsertAsync(staff, "Ledger", new Dictionary<string, object?>
            { ["code"] = code, ["ledger_name"] = name, ["parent_ledger"] = parent, ["is_group"] = group, ["root_type"] = root });
        await Add("1000", "Assets", null, true);
        await Add("1100", "Current assets", "1000", true);
        await Add("1110", "Cash", "1100", false);
        await Add("1120", "Bank", "1100", false);
        await Add("1200", "Fixed assets", "1000", true);
        await Add("2000", "Liabilities", null, true, "Liability");
        await Add("2100", "Payables", "2000", false, "Liability");

        var rows = (await docs.ListAsync(staff, "Ledger", new ListRequest(), allowAllReadableFields: true)).Items;
        var roots = TreeView.Build(settings, rows);
        Assert.True(roots.Select(r => r.Name).SequenceEqual(["1000", "2000"]), "roots in list order");
        var assets = roots[0];
        Assert.True(assets.Children.Select(c => c.Name).SequenceEqual(["1100", "1200"]), "children in list order");
        Assert.Equal(4, assets.Descendants, "all levels counted");
        Assert.Equal(2, assets.Children[0].Children[1].Depth, "depth");
        Assert.True(assets.IsGroup && !assets.Children[0].Children[0].IsGroup, "group flag");
        Assert.Equal("Current assets", assets.Children[0].Title, "title field");

        // Expansion helpers.
        var all = TreeView.ExpandedTo(roots, int.MaxValue);
        Assert.True(all.SetEquals(["1000", "1100", "2000"]), "only nodes with children are expandable: " + string.Join(",", all));
        Assert.True(TreeView.Visible(roots, new HashSet<string>()).Select(n => n.Name).SequenceEqual(["1000", "2000"]), "collapsed");
        Assert.True(TreeView.Visible(roots, new HashSet<string> { "1000" }).Select(n => n.Name).SequenceEqual(["1000", "1100", "1200", "2000"]), "one level open");
        Assert.True(TreeView.Visible(roots, all).Select(n => n.Name).SequenceEqual(["1000", "1100", "1110", "1120", "1200", "2000", "2100"]), "depth-first order");
        Assert.True(TreeView.ExpandedTo(roots, 1).SetEquals(["1000", "2000"]), "first level");
        Assert.Equal("1100", TreeView.ParentOf(roots, assets.Children[0].Children[0])?.Name, "parent lookup for Left arrow");

        // A filter that drops the parents: the children stay visible at the top, marked.
        var filtered = (await docs.ListAsync(staff, "Ledger", new ListRequest([new FilterClause("is_group", "=", false)]), allowAllReadableFields: true)).Items;
        var leafRoots = TreeView.Build(settings, filtered);
        Assert.True(leafRoots.Count == 3 && leafRoots.All(r => r.ParentMissing), "orphans become marked roots");

        // Bad data with a parent loop (A → B → A) and a self parent: every row shown once, nothing hangs.
        IReadOnlyDictionary<string, object?> Row(string name, string? parent) => new Dictionary<string, object?> { ["name"] = name, ["parent_ledger"] = parent };
        var looped = TreeView.Build(settings, [Row("A", "B"), Row("B", "A"), Row("C", "B"), Row("S", "S")]);
        var shown = TreeView.All(looped).Select(n => n.Name).ToList();
        Assert.True(shown.OrderBy(x => x).SequenceEqual(["A", "B", "C", "S"]), "each row once: " + string.Join(",", shown));
        Assert.True(looped.Single(r => r.Name == "A").InCycle, "the loop is cut at its first row and flagged");
        Assert.True(looped.Single(r => r.Name == "S") is { InCycle: false, ParentMissing: false }, "a self parent counts as no parent");
    }

    [Test]
    public async Task Tree_settings_are_configurable_and_validated()
    {
        await using (var ok = BuildTreeProvider(""", "tree": { "parentField": "previous_version" }"""))
        {
            Assert.Equal(new TreeView.Settings("previous_version", null), TreeView.Resolve(ok.GetRequiredService<IMetadataProvider>().Current.Get("Ledger")), "configured parent, no group field");
        }
        await using var bad = BuildTreeProvider(""", "tree": { "parentField": "code", "groupField": "root_type" }""");
        var ex = Assert.Throws<MetadataCompilationException>(() => bad.GetRequiredService<IMetadataProvider>(), "invalid tree settings are rejected");
        var paths = ex.Errors.Select(e => e.Path).ToList();
        Assert.True(paths.Contains("listView.tree.parentField") && paths.Contains("listView.tree.groupField"), string.Join("; ", ex.Errors));
    }
}
