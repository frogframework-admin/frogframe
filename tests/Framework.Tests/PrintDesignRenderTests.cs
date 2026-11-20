using Framework.Documents;
using Framework.Printing;
using Framework.Security;
using Framework.Web.Printing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// P3-2 end to end: a designed letterhead and print format change what PrintService builds, what the HTML (and so
/// the PDF) contains, without bypassing field permissions; without designs the print is unchanged.
/// </summary>
sealed partial class Tests
{
    [Test]
    public async Task Print_uses_designed_letterhead_and_format_in_html_and_pdf()
    {
        await using var provider = BuildPrintProvider(s =>
        {
            s.TryAddSingleton<ILetterheadProvider, ConfigLetterheadProvider>();
            s.TryAddSingleton<PrintService>();
            s.TryAddSingleton<PrintHtmlRenderer>();
            s.TryAddSingleton<IPdfRenderer, ChromiumPdfRenderer>();
        }, new() { ["Print:Letterheads:acme:CompanyName"] = "PT Config Letterhead" });
        var docs = provider.GetRequiredService<DocumentService>();
        var printer = provider.GetRequiredService<PrintService>();
        var html = provider.GetRequiredService<PrintHtmlRenderer>();
        var designs = provider.GetRequiredService<PrintDesignService>();
        var ana = new UserContext("ana", "Ana Sales", "acme", new HashSet<string> { "Sales" }, new Dictionary<string, string>());
        var fin = new UserContext("fia", "Fia Finance", "acme", new HashSet<string> { "Finance" }, new Dictionary<string, string>());

        var inv = await docs.InsertAsync(ana, "PrintInvoice", new Dictionary<string, object?>
        {
            ["customer"] = "PT Pelanggan", ["invoice_date"] = "2026-11-20", ["reference"] = "PO-7781", ["total"] = 1_500_000m,
            ["items"] = new List<Dictionary<string, object?>>
            {
                new() { ["item"] = "Consulting", ["qty"] = 2, ["rate"] = 500_000m, ["amount"] = 1_000_000m },
                new() { ["item"] = "Support", ["qty"] = 1, ["rate"] = 500_000m, ["amount"] = 500_000m },
            },
            ["notes"] = new List<Dictionary<string, object?>> { new() { ["note"] = "Handle with care" } },
        });
        inv = await docs.UpdateAsync(PrintAdmin, "PrintInvoice", inv.Name, inv.Version, new Dictionary<string, object?> { ["cost"] = 900_000m });
        inv = await docs.RunActionAsync(ana, "PrintInvoice", inv.Name, "Approve", inv.Version, null);

        // Without designs: the configured letterhead and the built-in layout, as before.
        var plain = await printer.BuildAsync(ana, "PrintInvoice", inv.Name);
        Assert.True(plain is { FormatName: null, Paper: "A4", Landscape: false, ShowLetterhead: true, Accent: null, FieldColumns: 2 }, "built-in page");
        Assert.Equal("PT Config Letterhead", plain.Letterhead.CompanyName, "letterhead from configuration");
        Assert.Equal(2, plain.Tables.Count, "both tables");
        Assert.True(plain.Sections.SelectMany(s => s.Fields).All(f => f.Label != "Internal cost"), "permission level 1 hidden from Sales");

        // Design a letterhead and the default format.
        await designs.SaveLetterheadAsync(PrintAdmin, null, new LetterheadDesign
        {
            Name = "Main", CompanyName = "PT Acme Nusantara", Address = "Jl. Sudirman 1, Jakarta", LogoDataUri = TinyPngDataUri, Align = "center",
            Accent = "#aa3300", FooterText = "Bank BCA 123 </style><script>alert(1)</script>",
        }, null);
        await designs.SaveFormatAsync(PrintAdmin, null, new PrintFormatDesign
        {
            DocType = "PrintInvoice", Name = "Standard", Paper = "Letter", Landscape = true, Accent = "#0055aa", FieldColumns = 3,
            HiddenTables = ["notes"],
            Format = new()
            {
                Title = "Tax Invoice",
                Sections = [new() { Title = "Billing", Fields = ["reference", "customer", "cost"] }],
                TableColumns = new() { ["items"] = ["item", "amount"] },
                TotalFields = ["total"], AmountInWordsField = "total",
                Signatures = [new() { Label = "Approved by", ToStates = ["Approved"] }],
                FooterNote = "Thank you.",
            },
        }, null);
        await designs.SaveFormatAsync(PrintAdmin, null, new PrintFormatDesign { DocType = "PrintInvoice", Name = "Customer copy", ShowLetterhead = false }, null);

        var m = await printer.BuildAsync(ana, "PrintInvoice", inv.Name);
        Assert.True(m is { FormatName: "Standard", Paper: "Letter", Landscape: true, Accent: "#0055aa", FieldColumns: 3, Title: "Tax Invoice" }, "designed page");
        Assert.True(m.Letterhead is { CompanyName: "PT Acme Nusantara", Align: "center", Accent: "#aa3300" } && m.Letterhead.LogoDataUri == TinyPngDataUri, "designed letterhead");
        Assert.True(m.Sections.Single().Fields.Select(f => f.Label).SequenceEqual(["Reference", "Customer"]), "block order; cost still hidden from Sales");
        Assert.True(m.Tables.Count == 1 && m.Tables[0].Columns.Select(c => c.Label).SequenceEqual(["Item", "Amount"]), "notes hidden, item columns chosen");
        Assert.True(m.Totals.Single().Label == "Total" && m.AmountInWords is { Length: > 0 }, "totals and words");
        Assert.Equal("Ana Sales", m.Signatures.Single().Name, "signature from the workflow");
        var mf = await printer.BuildAsync(fin, "PrintInvoice", inv.Name);
        Assert.True(mf.Sections.Single().Fields.Any(f => f.Label == "Internal cost"), "Finance may read permission level 1, so it prints for them");
        Assert.True((await printer.BuildAsync(ana, "PrintInvoice", inv.Name, "Customer copy")) is { FormatName: "Customer copy", ShowLetterhead: false }, "named format");

        // HTML: what the browser print view and the PDF use.
        var page = await html.RenderAsync(m);
        foreach (var expected in new[] { "size: Letter landscape", "--accent:#0055aa", "--lh-accent:#aa3300", "lh align-center", "<img src=\"data:image/png;base64,",
                     "repeat(3, minmax(0, 1fr))", "Tax Invoice", "PT Acme Nusantara", "Thank you." })
            Assert.True(page.Contains(expected), $"HTML contains {expected}");
        Assert.True(!page.Contains("Handle with care") && !page.Contains("<script>"), "hidden table not printed; letterhead text cannot inject markup");
        var plainPage = await html.RenderAsync(plain);
        Assert.True(plainPage.Contains("size: A4 portrait") && plainPage.Contains("PT Config Letterhead") && plainPage.Contains("style=\"--sheet-w:210mm;--sheet-h:297mm;\"") && !plainPage.Contains("--title:"), "built-in page unchanged");

        // PDF through headless Chrome when the server has it.
        try
        {
            var pdf = await provider.GetRequiredService<IPdfRenderer>().RenderAsync(page);
            Assert.True(pdf.Length > 1000 && System.Text.Encoding.ASCII.GetString(pdf, 0, 5) == "%PDF-", "PDF rendered");
            if (Environment.GetEnvironmentVariable("FROGFRAME_TEST_PDF_OUT") is { Length: > 0 } outPath) await File.WriteAllBytesAsync(outPath, pdf);
        }
        catch (PdfUnavailableException) { /* no Chrome on this machine: HTML checks above still apply */ }
    }
}
