using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence.PostgreSql;
using Framework.Printing;
using Framework.Security;
using Framework.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Letterhead & print format designer (P3-2), storage and rules: System Manager only, validation against the
/// DocType (fields, table columns, number totals, workflow states), safe logos and colours, one default per
/// kind/DocType, default promotion on delete, optimistic concurrency, and (with FROGFRAME_TEST_PG) the PostgreSQL store.
/// </summary>
sealed partial class Tests
{
    internal static ServiceProvider BuildPrintProvider(Action<IServiceCollection>? extra = null, Dictionary<string, string?>? config = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "frogframe-print-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "PrintInvoice.doctype.json"), """
        {
          "name": "PrintInvoice", "version": 1, "title": "Invoice", "module": "CORE", "workflow": "PrintInvoiceFlow",
          "naming": { "pattern": "INV-{#####}" },
          "listView": { "titleField": "customer", "searchFields": ["name", "customer"] },
          "fields": [
            { "name": "customer", "type": "Data", "label": "Customer", "requiredWhen": "true" },
            { "name": "invoice_date", "type": "Date", "label": "Invoice date" },
            { "name": "reference", "type": "Data", "label": "Reference" },
            { "name": "items", "type": "Table", "label": "Items", "childDocType": "PrintInvoiceItem" },
            { "name": "notes", "type": "Table", "label": "Notes", "childDocType": "PrintInvoiceNote" },
            { "name": "total", "type": "Currency", "label": "Total" },
            { "name": "cost", "type": "Currency", "label": "Internal cost", "permLevel": 1 }
          ],
          "permissions": [
            { "role": "Sales", "actions": ["read", "create", "write"], "scope": "all" },
            { "role": "Finance", "actions": ["read"], "scope": "all" },
            { "role": "Finance", "actions": ["read"], "permLevel": 1, "scope": "all" }
          ]
        }
        """);
        File.WriteAllText(Path.Combine(dir, "PrintInvoiceItem.doctype.json"), """
        { "name": "PrintInvoiceItem", "version": 1, "title": "Invoice Item", "isChild": true,
          "fields": [ { "name": "item", "type": "Data", "label": "Item" }, { "name": "qty", "type": "Int", "label": "Qty" },
                      { "name": "rate", "type": "Currency", "label": "Rate" }, { "name": "amount", "type": "Currency", "label": "Amount" } ] }
        """);
        File.WriteAllText(Path.Combine(dir, "PrintInvoiceNote.doctype.json"), """
        { "name": "PrintInvoiceNote", "version": 1, "title": "Invoice Note", "isChild": true,
          "fields": [ { "name": "note", "type": "Data", "label": "Note" } ] }
        """);
        File.WriteAllText(Path.Combine(dir, "PrintInvoiceFlow.workflow.json"), """
        { "name": "PrintInvoiceFlow", "docType": "PrintInvoice", "version": 1, "initialState": "Draft",
          "states": [ { "name": "Draft", "docStatus": 0 }, { "name": "Approved", "docStatus": 1, "tone": "positive" } ],
          "transitions": [ { "from": "Draft", "to": "Approved", "action": "Approve", "allowedRoles": ["Sales"] } ] }
        """);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config ?? new()).Build());
        extra?.Invoke(services);
        services.AddFrogFramework(o => o.LoadFrom(dir));
        return services.BuildServiceProvider();
    }

    internal static readonly UserContext PrintAdmin = new("root", "Root", "acme", new HashSet<string> { UserContext.SystemManager }, new Dictionary<string, string>());
    internal const string TinyPngDataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Test]
    public async Task PrintDesigns_validate_and_keep_one_default()
    {
        await using var provider = BuildPrintProvider();
        var designs = provider.GetRequiredService<PrintDesignService>();
        var sales = new UserContext("ana", "Ana", "acme", new HashSet<string> { "Sales" }, new Dictionary<string, string>());

        // Only System Managers change designs.
        await Assert.Throws<PermissionDeniedException>(() => designs.SaveLetterheadAsync(sales, null, new LetterheadDesign { Name = "X", CompanyName = "X" }, null), "sales cannot");

        // Letterheads: validation, first one becomes default, one default only.
        var bad = await Assert.Throws<ValidationFailedException>(() => designs.SaveLetterheadAsync(PrintAdmin, null, new LetterheadDesign
        {
            Name = "", CompanyName = "", Align = "middle", Accent = "red;}body{x:y", LogoDataUri = "data:text/html;base64,PHNjcmlwdD4=",
        }, null), "invalid letterhead");
        Assert.True(new[] { "name", "companyName", "align", "accent", "logo" }.All(p => bad.Errors.Any(e => e.Path == p)), string.Join("; ", bad.Errors));
        var bigLogo = "data:image/png;base64," + new string('A', PrintDesignService.MaxLogoBytes * 2);
        await Assert.Throws<ValidationFailedException>(() => designs.SaveLetterheadAsync(PrintAdmin, null, new LetterheadDesign { Name = "Big", CompanyName = "X", LogoDataUri = bigLogo }, null), "logo too large");

        var main = await designs.SaveLetterheadAsync(PrintAdmin, null, new LetterheadDesign { Name = "Main", CompanyName = "PT Acme", LogoDataUri = TinyPngDataUri }, null);
        Assert.True(main.Design.IsDefault, "first letterhead is the default");
        var branch = await designs.SaveLetterheadAsync(PrintAdmin, null, new LetterheadDesign { Name = "Branch", CompanyName = "PT Acme Surabaya", IsDefault = true, Align = "center" }, null);
        var all = await designs.LetterheadsAsync("acme");
        Assert.True(all.Single(l => l.Design.Name == "Branch").Design.IsDefault && !all.Single(l => l.Design.Name == "Main").Design.IsDefault, "default moved to Branch");
        Assert.Equal("Branch", (await designs.LetterheadAsync("acme", null))?.Name, "default lookup");
        Assert.Equal("Main", (await designs.LetterheadAsync("acme", "Main"))?.Name, "named lookup");
        await Assert.Throws<ValidationFailedException>(() => designs.SaveLetterheadAsync(PrintAdmin, null, new LetterheadDesign { Name = "main", CompanyName = "Dup" }, null), "duplicate name");
        await Assert.Throws<ConcurrencyException>(() => designs.SaveLetterheadAsync(PrintAdmin, main.Id, main.Design with { Contact = "x" }, 99), "stale version");
        await designs.DeleteAsync(PrintAdmin, branch.Id);
        Assert.True((await designs.LetterheadAsync("acme", null))?.Name == "Main", "deleting the default promotes the next one");

        // Formats: every reference is checked against the DocType.
        var errors = designs.Validate(new PrintFormatDesign
        {
            DocType = "PrintInvoice", Name = "Bad", Paper = "A3", FieldColumns = 4, Accent = "blue",
            HiddenTables = ["nope"],
            Format = new PrintFormatDef
            {
                Sections = [new PrintSectionDef { Fields = ["customer", "items", "ghost"] }],
                TableColumns = new() { ["items"] = ["item", "colour"], ["notes"] = [] },
                TotalFields = ["customer"], AmountInWordsField = "reference",
                Signatures = [new PrintSignatureDef { Label = "Boss", ToStates = ["Paid"] }],
            },
        });
        string Joined() => string.Join(" | ", errors.Select(e => $"{e.Path}: {e.Message}"));
        foreach (var expected in new[] { "paper", "fieldColumns", "accent", "hiddenTables", "totalFields", "amountInWordsField", "signatures" })
            Assert.True(errors.Any(e => e.Path == expected), $"{expected} reported: {Joined()}");
        Assert.True(errors.Count(e => e.Path == "sections[0]") == 2, "table and unknown field in a block: " + Joined());
        Assert.True(errors.Count(e => e.Path == "tableColumns") == 2, "unknown column and empty column list: " + Joined());
        Assert.True(designs.Validate(new PrintFormatDesign { DocType = "PrintInvoiceItem", Name = "x" }).Any(e => e.Path == "docType"), "child DocTypes cannot have formats");

        var std = await designs.SaveFormatAsync(PrintAdmin, null, new PrintFormatDesign { DocType = "PrintInvoice", Name = "Standard" }, null);
        Assert.True(std.Design.IsDefault, "first format is the default");
        await Assert.Throws<ValidationFailedException>(() => designs.SaveFormatAsync(PrintAdmin, null, new PrintFormatDesign { DocType = "PrintInvoice", Name = "Copy", Letterhead = "Gone" }, null), "unknown letterhead");
        var copy = await designs.SaveFormatAsync(PrintAdmin, null, new PrintFormatDesign { DocType = "PrintInvoice", Name = "Customer copy", Letterhead = "Main" }, null);
        Assert.True(!copy.Design.IsDefault, "second format is not the default");
        Assert.Equal("Standard", (await designs.FormatAsync("acme", "PrintInvoice", null))?.Name, "default format");
        Assert.Equal("Customer copy", (await designs.FormatAsync("acme", "PrintInvoice", "Customer copy"))?.Name, "named format");
        Assert.Equal("Standard", (await designs.FormatAsync("acme", "PrintInvoice", "Unknown"))?.Name, "unknown name falls back to the default");
        Assert.True((await designs.FormatAsync("globex", "PrintInvoice", null)) is null, "per tenant");
    }

    [Test]
    public async Task PrintDesigns_postgres_store_round_trip()
    {
        var pg = Environment.GetEnvironmentVariable("FROGFRAME_TEST_PG");
        if (string.IsNullOrEmpty(pg)) return; // runs with a PostgreSQL test database
        await using var provider = BuildPrintProvider(s => s.AddFrogFrameworkPostgreSql(pg));
        await provider.MigrateFrogFrameworkDatabaseAsync();
        Assert.True(provider.GetRequiredService<IPrintDesignStore>() is PostgresPrintDesignStore, "PostgreSQL store registered");
        var designs = provider.GetRequiredService<PrintDesignService>();
        var admin = PrintAdmin with { TenantId = "print-" + Guid.NewGuid().ToString("N")[..8] };
        var lh = await designs.SaveLetterheadAsync(admin, null, new LetterheadDesign { Name = "Main", CompanyName = "PT PG", LogoDataUri = TinyPngDataUri, Align = "right", Accent = "#123456" }, null);
        var back = (await designs.LetterheadsAsync(admin.TenantId)).Single();
        Assert.True(back.Design is { CompanyName: "PT PG", Align: "right", Accent: "#123456", IsDefault: true } && back.Design.LogoDataUri == TinyPngDataUri, "letterhead JSON round trip");
        var f1 = await designs.SaveFormatAsync(admin, null, new PrintFormatDesign
        {
            DocType = "PrintInvoice", Name = "Standard", Paper = "Letter", Landscape = true, FieldColumns = 3, HiddenTables = ["notes"],
            Format = new PrintFormatDef { Sections = [new PrintSectionDef { Title = "Who", Fields = ["customer", "name"] }], TotalFields = ["total"],
                TableSecondLine = new() { ["items"] = ["rate"] } },
        }, null);
        var f2 = await designs.SaveFormatAsync(admin, null, new PrintFormatDesign { DocType = "PrintInvoice", Name = "Copy", IsDefault = true }, null);
        var listed = await designs.FormatsAsync(admin.TenantId, "PrintInvoice");
        Assert.True(listed.Single(f => f.Design.Name == "Copy").Design.IsDefault && !listed.Single(f => f.Design.Name == "Standard").Design.IsDefault, "default switched in SQL");
        var std = listed.Single(f => f.Design.Name == "Standard");
        Assert.True(std.Design is { Paper: "Letter", Landscape: true, FieldColumns: 3 } && std.Design.HiddenTables.SequenceEqual(["notes"])
                    && std.Design.Format.Sections![0].Fields.SequenceEqual(["customer", "name"]) && std.Version == 2
                    && std.Design.Format.TableSecondLine!["items"].SequenceEqual(["rate"]), "format round trip (version bumped by demotion)");
        await Assert.Throws<ConcurrencyException>(() => designs.SaveFormatAsync(admin, f1.Id, f1.Design, 1), "stale version in SQL");
        await designs.DeleteAsync(admin, f2.Id);
        Assert.True((await designs.FormatAsync(admin.TenantId, "PrintInvoice", null))?.Name == "Standard", "promotion after delete");
        await designs.DeleteAsync(admin, lh.Id);
        Assert.Equal(0, (await designs.LetterheadsAsync(admin.TenantId)).Count, "deleted");
    }
}
