using System.Text.Json;
using Framework.Web.AI;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Verification for Phase 1 (<see cref="AiKnowledgeIndex"/>) and Phase 2 (<see cref="AiAssistantTools"/>)
/// of the End-User AI Assistant.
/// </summary>
sealed partial class Tests
{
    [Test]
    public Task AiKnowledgeIndex_loads_bundled_guides_and_compiled_xml_summaries()
    {
        var index = sp.GetRequiredService<AiKnowledgeIndex>();

        Assert.True(index.Chunks.Any(c => c.Kind == "Guide"), "bundled Markdown guides are indexed at runtime");
        Assert.True(index.Chunks.Any(c => c.Kind == "CodeSummary" && c.Source == "Apps.Erp.xml"),
            "compiled C# XML doc summaries (Apps.Erp.xml) are indexed at runtime");

        var periodHits = index.Search("AccountingPeriod", maxResults: 8);
        Assert.True(periodHits.Count > 0, "search finds AccountingPeriod chunks");
        Assert.True(periodHits.Any(h => h.Section.Contains("AccountingPeriodRulesHook") || h.Content.Contains("AccountingPeriod")),
            "search matches C# hook summary or guide for AccountingPeriod");

        var tbHits = index.Search("Trial Balance", maxResults: 5);
        Assert.True(tbHits.Count > 0, "search finds Trial Balance documentation");
        return Task.CompletedTask;
    }

    [Test]
    public Task AiAssistantTools_explains_doctype_status_semantics_and_enforces_permissions()
    {
        var dewiTools = AiAssistantTools.ForUser(sp, U("dewi")); // Finance + Employee in acme
        var andiTools = AiAssistantTools.ForUser(sp, U("andi")); // Employee only in acme

        // 1. Master data DocType (Vendor: allowSubmit = false, no workflow)
        using var vendorDoc = JsonDocument.Parse(dewiTools.ExplainDocType("Vendor"));
        Assert.True(vendorDoc.RootElement.GetProperty("canRead").GetBoolean(), "dewi can read Vendor");
        Assert.True(!vendorDoc.RootElement.GetProperty("allowSubmit").GetBoolean(), "Vendor is not submittable");
        var vendorSemantics = vendorDoc.RootElement.GetProperty("statusSemantics").GetString() ?? "";
        Assert.True(vendorSemantics.Contains("Master Data", StringComparison.OrdinalIgnoreCase),
            "Vendor status semantics explain that Draft badge on Master Data means active and editable");

        // 2. Submittable transaction DocType (JournalEntry: allowSubmit = true)
        using var jeDoc = JsonDocument.Parse(dewiTools.ExplainDocType("JournalEntry"));
        Assert.True(jeDoc.RootElement.GetProperty("canRead").GetBoolean(), "dewi (Finance) can read JournalEntry");
        Assert.True(jeDoc.RootElement.GetProperty("allowSubmit").GetBoolean(), "JournalEntry is submittable");
        var jeSemantics = jeDoc.RootElement.GetProperty("statusSemantics").GetString() ?? "";
        Assert.True(jeSemantics.Contains("Submittable Transaction", StringComparison.OrdinalIgnoreCase),
            "JournalEntry status semantics explain that Draft does not post to the General Ledger until Submitted");

        // 3. Permission check: andi (Employee) cannot read JournalEntry
        using var deniedDoc = JsonDocument.Parse(andiTools.ExplainDocType("JournalEntry"));
        Assert.True(!deniedDoc.RootElement.GetProperty("canRead").GetBoolean(), "andi cannot read JournalEntry");
        return Task.CompletedTask;
    }

    [Test]
    public async Task AiAssistantTools_navigation_and_document_inspection_enforce_role_and_tenant_isolation()
    {
        var adminTools = AiAssistantTools.ForUser(sp, U("admin"));
        var dewiTools = AiAssistantTools.ForUser(sp, U("dewi")); // tenant acme
        var tonoTools = AiAssistantTools.ForUser(sp, U("tono")); // tenant globex

        // 1. Navigation discovery respects admin vs regular user roles
        using var adminNav = JsonDocument.Parse(await adminTools.FindNavigationAsync("template"));
        var adminUrls = adminNav.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("url").GetString()).ToList();
        Assert.True(adminUrls.Contains("/admin/report-templates"), "admin sees /admin/report-templates");

        using var dewiNav = JsonDocument.Parse(await dewiTools.FindNavigationAsync("template"));
        var dewiUrls = dewiNav.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => e.GetProperty("url").GetString()).ToList();
        Assert.True(!dewiUrls.Contains("/admin/report-templates"), "non-admin does not see /admin/report-templates");

        // 2. Live document inspection in tenant acme vs tenant globex
        var acmeVendorJson = await dewiTools.InspectDocumentAsync("Vendor", "VND-0001");
        using var acmeVendor = JsonDocument.Parse(acmeVendorJson);
        Assert.True(acmeVendor.RootElement.TryGetProperty("name", out var nameProp), $"expected 'name' in: {acmeVendorJson}");
        Assert.Equal("VND-0001", nameProp.GetString(), "dewi can inspect VND-0001 in acme");
        Assert.True(acmeVendor.RootElement.TryGetProperty("statusExplanation", out var expProp), $"expected 'statusExplanation' in: {acmeVendorJson}");
        Assert.True((expProp.GetString() ?? "").Contains("Master Data"),
            "InspectDocument explains Master Data Draft status");

        var globexVendorJson = await tonoTools.InspectDocumentAsync("Vendor", "VND-0001");
        using var globexVendor = JsonDocument.Parse(globexVendorJson);
        Assert.Equal("Globex Supplies", globexVendor.RootElement.GetProperty("data").GetProperty("vendor_name").GetString(),
            "tono in globex sees only globex's VND-0001, never acme's");

        var globexMissingJson = await tonoTools.InspectDocumentAsync("Vendor", "VND-0002");
        using var globexMissing = JsonDocument.Parse(globexMissingJson);
        Assert.True(globexMissing.RootElement.TryGetProperty("found", out var foundProp) && !foundProp.GetBoolean(),
            $"tono in globex cannot inspect acme-only document VND-0002: {globexMissingJson}");

        // 3. Diagnose records summarizes counts by status
        var diagJson = await dewiTools.DiagnoseDocTypeRecordsAsync("PurchaseRequest");
        using var diag = JsonDocument.Parse(diagJson);
        Assert.True(diag.RootElement.TryGetProperty("totalVisibleRecords", out var totalProp) && totalProp.GetInt32() > 0,
            $"DiagnoseDocTypeRecords returns visible PurchaseRequest count for dewi: {diagJson}");
    }
}
