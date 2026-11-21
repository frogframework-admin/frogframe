using Framework.Metadata;
using Framework.Documents;
using Framework.Printing;
using Framework.Security;
using Framework.Web;
using Framework.Web.Printing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// A wide child table (shaped like a sales order line) printed without a print format must fit an A4 portrait page:
/// default columns skip Check fields and a Link's repeated *_name, wide tables get a denser type scale, and the
/// key/value grid and table cells shrink and wrap instead of pushing past the content width. A print format can
/// instead move chosen columns to a second line under each row (two-line layout).
/// </summary>
sealed partial class Tests
{
    [Test]
    public async Task Print_wide_item_table_fits_the_page_by_default()
    {
        var dir = Path.Combine(Path.GetTempPath(), "frogframe-printfit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "FitMaster.doctype.json"), """
        { "name": "FitMaster", "version": 1, "title": "Master", "module": "CORE", "naming": { "pattern": "field:code" },
          "fields": [ { "name": "code", "type": "Data", "label": "Code", "requiredWhen": "true" } ],
          "permissions": [ { "role": "Sales", "actions": ["read", "create"], "scope": "all" } ] }
        """);
        File.WriteAllText(Path.Combine(dir, "FitLine.doctype.json"), """
        { "name": "FitLine", "version": 1, "title": "Line", "isChild": true,
          "fields": [ { "name": "item", "type": "Link", "label": "Item", "linkTo": "FitMaster" },
                      { "name": "item_name", "type": "Data", "label": "Item name" },
                      { "name": "description", "type": "Text", "label": "Description" },
                      { "name": "qty", "type": "Float", "label": "Quantity" },
                      { "name": "uom", "type": "Link", "label": "UOM", "linkTo": "FitMaster" },
                      { "name": "rate", "type": "Currency", "label": "Rate" },
                      { "name": "discount_percent", "type": "Percent", "label": "Discount (%)" },
                      { "name": "amount", "type": "Currency", "label": "Amount" },
                      { "name": "is_vat_applicable", "type": "Check", "label": "Subject to VAT" },
                      { "name": "taxable_amount", "type": "Currency", "label": "Line tax base" } ] }
        """);
        File.WriteAllText(Path.Combine(dir, "FitOrder.doctype.json"), """
        { "name": "FitOrder", "version": 1, "title": "Sales Order", "module": "CORE", "naming": { "pattern": "SO-{#####}" },
          "fields": [ { "name": "customer", "type": "Data", "label": "Customer" },
                      { "name": "terms", "type": "Data", "label": "Terms & conditions" },
                      { "name": "items", "type": "Table", "label": "Items", "childDocType": "FitLine" } ],
          "permissions": [ { "role": "Sales", "actions": ["read", "create", "write"], "scope": "all" } ] }
        """);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.TryAddSingleton<ILetterheadProvider, ConfigLetterheadProvider>();
        services.TryAddSingleton<PrintService>();
        services.TryAddSingleton<PrintHtmlRenderer>();
        services.AddFrogFramework(o => o.LoadFrom(dir));
        await using var sp = services.BuildServiceProvider();
        var docs = sp.GetRequiredService<DocumentService>();
        var printer = sp.GetRequiredService<PrintService>();
        var html = sp.GetRequiredService<PrintHtmlRenderer>();
        var u = new UserContext("ana", "Ana", "acme", new HashSet<string> { "Sales", UserContext.SystemManager }, new Dictionary<string, string>());

        await docs.InsertAsync(u, "FitMaster", new Dictionary<string, object?> { ["code"] = "LAP-14" });
        await docs.InsertAsync(u, "FitMaster", new Dictionary<string, object?> { ["code"] = "UNT" });
        var line = new Dictionary<string, object?>
        {
            ["item"] = "LAP-14", ["item_name"] = "Laptop Business Ultra 14", ["description"] = "Laptop kerja bisnis 14 inch", ["qty"] = 2m,
            ["uom"] = "UNT", ["rate"] = 22_500_000m, ["discount_percent"] = 0m, ["amount"] = 45_000_000m, ["is_vat_applicable"] = true, ["taxable_amount"] = 45_000_000m,
        };
        var order = await docs.InsertAsync(u, "FitOrder", new Dictionary<string, object?>
        {
            ["customer"] = "PT Telekomunikasi Indonesia Tbk (CUST-000001)",
            ["terms"] = "Pengiriman ke Telkom Landmark Tower Lt. 15, Jl. Jend. Gatot Subroto Kav. 52, Jakarta Selatan.",
            ["items"] = new List<Dictionary<string, object?>> { line },
        });

        // No print format: redundant columns are dropped and the table is marked dense.
        var model = await printer.BuildAsync(u, "FitOrder", order.Name);
        var cols = model.Tables.Single().Columns.Select(c => c.Label).ToList();
        Assert.True(!cols.Contains("Subject to VAT"), "Check fields are not default columns");
        Assert.True(!cols.Contains("Item name"), "a Link's *_name is not repeated next to the Link");
        Assert.Equal(8, cols.Count, "eight meaningful default columns");
        var page = await html.RenderAsync(model);
        Assert.True(page.Contains("class=\"items denser\""), "eight or more columns use the denser scale");

        // A format with few columns keeps the normal size; a format can still print every column.
        var narrow = await printer.BuildPreviewAsync(u, "FitOrder", order.Name, new PrintFormatDesign
        {
            DocType = "FitOrder", Name = "Narrow", Format = new() { TableColumns = new() { ["items"] = ["item", "qty", "amount"] } },
        });
        Assert.Equal("items", System.Text.RegularExpressions.Regex.Match(await html.RenderAsync(narrow), "<table class=\"(items[^\"]*)\"").Groups[1].Value.Trim(), "few columns keep the normal size");
        var all = await printer.BuildPreviewAsync(u, "FitOrder", order.Name, new PrintFormatDesign
        {
            DocType = "FitOrder", Name = "All", Format = new() { TableColumns = new() { ["items"] = ["item", "item_name", "description", "qty", "uom", "rate", "discount_percent", "amount", "is_vat_applicable", "taxable_amount"] } },
        });
        Assert.Equal(10, all.Tables.Single().Columns.Count, "an explicit format still prints every chosen column");

        // Two-line layout: chosen columns go under the row with their labels; the first line keeps the rest.
        var twoLine = await printer.BuildPreviewAsync(u, "FitOrder", order.Name, new PrintFormatDesign
        {
            DocType = "FitOrder", Name = "Two lines", Format = new() { TableSecondLine = new() { ["items"] = ["description", "uom"] } },
        });
        Assert.Equal("Description, UOM", string.Join(", ", twoLine.Tables.Single().Columns.Where(c => c.SecondLine).Select(c => c.Label)), "second-line columns");
        var twoHtml = await html.RenderAsync(twoLine);
        var tableHtml = twoHtml[twoHtml.IndexOf("<table class=\"items", StringComparison.Ordinal)..];
        var head = tableHtml[..tableHtml.IndexOf("</thead>", StringComparison.Ordinal)];
        Assert.True(!head.Contains(">Description<") && !head.Contains(">UOM<") && head.Contains(">Rate<"), "second-line columns are not header columns");
        Assert.True(tableHtml.Contains("two-line") && tableHtml.Contains("class=\"below\""), "row has a second line");
        Assert.True(tableHtml.Contains(">Description</span> Laptop kerja bisnis 14 inch"), "second line carries label and value");
        Assert.Equal("items dense two-line", System.Text.RegularExpressions.Regex.Match(tableHtml, "<table class=\"([^\"]*)\"").Groups[1].Value.Trim(), "density follows first-line columns (6)");

        // Side by side stays the default; moving every column down is ignored when printing and rejected when saving.
        Assert.True(!(await html.RenderAsync(model)).Contains("class=\"below\""), "no format: side by side");
        var allDown = new PrintFormatDesign { DocType = "FitOrder", Name = "Bad", Format = new() { TableColumns = new() { ["items"] = ["item", "qty"] }, TableSecondLine = new() { ["items"] = ["item", "qty"] } } };
        Assert.True((await printer.BuildPreviewAsync(u, "FitOrder", order.Name, allDown)).Tables.Single().Columns.All(c => !c.SecondLine), "at least one first-line column");
        var designs = sp.GetRequiredService<PrintDesignService>();
        Assert.True(designs.Validate(allDown).Any(e => e.Path == "tableSecondLine"), "all columns on the second line is rejected");
        Assert.True(designs.Validate(new PrintFormatDesign { DocType = "FitOrder", Name = "x", Format = new() { TableSecondLine = new() { ["items"] = ["is_vat_applicable"] } } })
            .Any(e => e.Path == "tableSecondLine"), "a second-line column must be printed");
        Assert.True(!designs.Validate(new PrintFormatDesign { DocType = "FitOrder", Name = "ok", Format = new() { TableSecondLine = new() { ["items"] = ["description"] } } }).Any(), "valid two-line format");
        Assert.Equal("description, uom", string.Join(", ", PrintDefaults.SecondLine(sp.GetRequiredService<Framework.Metadata.IMetadataProvider>().Current.Get("FitLine"), ["item", "description", "qty", "uom", "rate", "amount"])), "suggestion: text columns after the first");

        // The same setting in DocType metadata (printFormat.tableSecondLine) is checked when metadata is compiled.
        var registry = sp.GetRequiredService<Framework.Metadata.MetadataRegistry>();
        DocTypeDef WithSecondLine(params string[] cols)
        {
            var def = Framework.Metadata.MetadataJson.ParseDocType(Framework.Metadata.MetadataJson.Serialize(registry.Current.Get("FitOrder").Def));
            def.PrintFormat = new() { TableSecondLine = new() { ["items"] = [.. cols] } };
            return def;
        }
        Assert.True(registry.Preview(WithSecondLine("description")).Valid, "metadata: valid second line");
        Assert.True(!registry.Preview(WithSecondLine("no_such_field")).Valid, "metadata: unknown second-line field rejected");

        // Grid tracks may shrink, long values wrap: nothing pushes past the page's content width.
        Assert.True(PrintStyles.Css.Contains("minmax(0, 1fr)"), "key/value grid tracks can shrink below their content");
        Assert.True(PrintStyles.Css.Contains("overflow-wrap: anywhere"), "long values wrap");
    }
}
