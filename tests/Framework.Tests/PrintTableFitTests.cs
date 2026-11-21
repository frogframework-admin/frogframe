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
/// key/value grid and table cells shrink and wrap instead of pushing past the content width.
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

        // Layout options. Two lines: each label above its value in field blocks.
        var stacked = await html.RenderAsync(await printer.BuildPreviewAsync(u, "FitOrder", order.Name,
            new PrintFormatDesign { DocType = "FitOrder", Name = "Stacked", TwoLineFields = true }));
        Assert.True(stacked.Contains("class=\"kv stacked\""), "two lines: stacked field blocks");
        Assert.True(!page.Contains("kv stacked"), "default: label beside its value");

        // Show as records: one block per row with label/value lines; empty values show a dash; amounts are not right-aligned.
        var records = await html.RenderAsync(await printer.BuildPreviewAsync(u, "FitOrder", order.Name,
            new PrintFormatDesign { DocType = "FitOrder", Name = "Records", TablesAsRecords = true }));
        Assert.True(records.Contains("<div class=\"record-head\">Line 1</div>"), "record heading per row");
        Assert.True(!records.Contains("<table class=\"items"), "no item table in record mode");
        Assert.True(records.Contains("<span class=\"k\">Amount</span><span class=\"v\">Rp45.000.000</span>"), "amount as a plain value line");
        Assert.True(records.Contains("<span class=\"k\">Discount (%)</span><span class=\"v\">0%</span>"), "every printed column is a line");
        Assert.True(page.Contains("<td class=\"num\">Rp45.000.000</td>"), "default: amounts right-aligned in the table");

        // Grid tracks may shrink, long values wrap: nothing pushes past the page's content width.
        Assert.True(PrintStyles.Css.Contains("minmax(0, 1fr)"), "key/value grid tracks can shrink below their content");
        Assert.True(PrintStyles.Css.Contains("overflow-wrap: anywhere"), "long values wrap");
    }
}
