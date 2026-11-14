using System.Text;
using Framework.Documents;
using Framework.Imports;
using Framework.Metadata;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Tests for P1-5: Export List (.xlsx &amp; .csv) and Bulk Actions (Bulk Submit, Bulk Cancel, Bulk Edit, Bulk Delete).</summary>
sealed partial class Tests
{
    [Test]
    public async Task P1_5_ExportList_xlsx_and_csv_respect_scope_filters_selected_items_and_formula_injection()
    {
        var admin = U("admin");
        var v1 = await Docs.InsertAsync(admin, "Vendor", P(
            ("vendor_name", "=SUM(1,2) UniqueExportMarker"),
            ("category", "Perangkat keras"),
            ("npwp", "012345678901000")));
        var v2 = await Docs.InsertAsync(admin, "Vendor", P(
            ("vendor_name", "CV Samudra Logistik"),
            ("category", "Jasa"),
            ("npwp", "023456789012000")));

        // 1. Export Excel (.xlsx) with selectedNames filter
        var xlsxOut = await Docs.ExportListAsync(
            admin,
            "Vendor",
            "xlsx",
            selectedNames: [v1.Name],
            visibleColumns: ["vendor_name", "category"]);
        Assert.Equal(1, xlsxOut.Rows, "xlsx row count matches selected item");
        Assert.True(xlsxOut.FileName.EndsWith(".xlsx", StringComparison.Ordinal), "xlsx filename extension");

        var sheets = SpreadsheetReader.Read(xlsxOut.Content, xlsxOut.FileName, 10 * 1024 * 1024);
        Assert.Equal(1, sheets.Count, "single worksheet generated");
        Assert.Equal(2, sheets[0].Rows.Count, "header row + 1 data row");
        Assert.Equal(v1.Name, sheets[0].Rows[1][0]?.ToString(), "exported document name matches");
        Assert.True(sheets[0].Rows[1][1]?.ToString()?.StartsWith("'=Sum(1,2)", StringComparison.OrdinalIgnoreCase) == true, "xlsx formula injection sanitized");

        // 2. Export CSV (.csv) with search filter
        var csvOut = await Docs.ExportListAsync(
            admin,
            "Vendor",
            "csv",
            new ListRequest(Search: "UniqueExportMarker"),
            visibleColumns: ["vendor_name", "category"]);
        Assert.Equal(1, csvOut.Rows, "csv row count matches search");
        var csvText = Encoding.UTF8.GetString(csvOut.Content).TrimStart('\uFEFF');
        Assert.True(csvText.Contains("UniqueExportMarker", StringComparison.OrdinalIgnoreCase), "csv contains matching vendor");
        Assert.True(!csvText.Contains("Samudra", StringComparison.Ordinal), "csv excludes non-matching vendor");
        Assert.True(csvText.Contains("'=Sum(1,2)", StringComparison.OrdinalIgnoreCase), "csv formula injection escaped with leading apostrophe");

        // 3. Role without export permission is rejected
        var guest = new UserContext("guest1", "Guest", "acme", new HashSet<string> { "NonExistentRole" }, new Dictionary<string, string>());
        await Assert.Throws<PermissionDeniedException>(() => Docs.ExportListAsync(guest, "Vendor", "csv"), "guest cannot export");
    }

    [Test]
    public async Task P1_5_BulkEdit_updates_permitted_field_across_drafts_and_reports_failures()
    {
        var admin = U("admin");
        var v1 = await Docs.InsertAsync(admin, "Vendor", P(
            ("vendor_name", "PT Satu"),
            ("category", "Perangkat keras"),
            ("npwp", "011111111111000")));
        var v2 = await Docs.InsertAsync(admin, "Vendor", P(
            ("vendor_name", "PT Dua"),
            ("category", "Perangkat keras"),
            ("npwp", "022222222222000")));

        var editable = Docs.BulkEditableFields(admin, sp.GetRequiredService<IMetadataProvider>().Current.Get("Vendor"));
        Assert.True(editable.Any(f => f.Name == "category"), "category is bulk-editable");

        var editRes = await Docs.BulkEditAsync(admin, "Vendor", [v1.Name, v2.Name, "VEN-DOES-NOT-EXIST"], "category", "Jasa");
        Assert.Equal(2, editRes.Succeeded.Count, "two valid vendors updated");
        Assert.Equal(1, editRes.Failed.Count, "missing vendor reported in Failed");
        Assert.Equal("VEN-DOES-NOT-EXIST", editRes.Failed[0].Name, "failed item name recorded");

        var updated1 = await Docs.GetAsync(admin, "Vendor", v1.Name);
        var updated2 = await Docs.GetAsync(admin, "Vendor", v2.Name);
        Assert.Equal("Jasa", updated1.Get("category")?.ToString(), "v1 category updated");
        Assert.Equal("Jasa", updated2.Get("category")?.ToString(), "v2 category updated");

        // Standard/unknown field cannot be bulk edited
        await Assert.Throws<InvalidPayloadException>(() =>
            Docs.BulkEditAsync(admin, "Vendor", [v1.Name], "docstatus", 1), "docstatus cannot be bulk edited");
    }

    [Test]
    public async Task P1_5_BulkSubmit_BulkCancel_and_BulkDelete_handle_workflow_and_standard_doctypes()
    {
        var admin = U("admin");
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var scope = sp.GetRequiredService<RoleCatalog>().Scope("acme");

        // 1. Standard submittable DocType (without workflow)
        reg.Publish(new DocTypeDef
        {
            Name = "BulkSubmittableNote",
            Title = "Bulk Submittable Note",
            Module = "CORE",
            Document = new DocumentDef { AllowSubmit = true, AllowCancel = true, AllowDelete = true },
            Naming = new NamingDef { Pattern = "BSN-{#####}" },
            Fields =
            [
                new FieldDef { Name = "title", Label = "Title", Type = "Data", RequiredWhen = "true", InListView = true },
                new FieldDef { Name = "notes", Label = "Notes", Type = "Data", InListView = true }
            ],
            Permissions =
            [
                new PermissionDef { Role = "System Manager", Actions = ["read", "create", "write", "submit", "cancel", "delete", "export"] }
            ]
        }, "admin", false, scope);

        var d1 = await Docs.InsertAsync(admin, "BulkSubmittableNote", P(("title", "Note A"), ("notes", "Initial")));
        var d2 = await Docs.InsertAsync(admin, "BulkSubmittableNote", P(("title", "Note B"), ("notes", "Initial")));
        var d3 = await Docs.InsertAsync(admin, "BulkSubmittableNote", P(("title", "Note C"), ("notes", "To delete")));

        // Bulk Submit d1 & d2
        var subRes = await Docs.BulkSubmitAsync(admin, "BulkSubmittableNote", [d1.Name, d2.Name]);
        Assert.Equal(2, subRes.Succeeded.Count, "both drafts submitted");
        Assert.Equal(0, subRes.Failed.Count, "zero submit failures");
        Assert.Equal(DocStatus.Submitted, (await Docs.GetAsync(admin, "BulkSubmittableNote", d1.Name)).DocStatus, "d1 is Submitted");
        Assert.Equal(DocStatus.Submitted, (await Docs.GetAsync(admin, "BulkSubmittableNote", d2.Name)).DocStatus, "d2 is Submitted");

        // Bulk Delete on [d1 (Submitted), d3 (Draft)]: d3 succeeds, d1 fails because it is Submitted
        var delRes = await Docs.BulkDeleteAsync(admin, "BulkSubmittableNote", [d1.Name, d3.Name]);
        Assert.Equal(1, delRes.Succeeded.Count, "draft d3 deleted");
        Assert.Equal(d3.Name, delRes.Succeeded[0], "d3 succeeded");
        Assert.Equal(1, delRes.Failed.Count, "submitted d1 cannot be deleted");
        Assert.Equal(d1.Name, delRes.Failed[0].Name, "d1 reported as failed");

        // Bulk Cancel d1 & d2
        var cancelRes = await Docs.BulkCancelAsync(admin, "BulkSubmittableNote", [d1.Name, d2.Name], "Bulk cancellation test");
        Assert.Equal(2, cancelRes.Succeeded.Count, "both submitted notes cancelled");
        Assert.Equal(0, cancelRes.Failed.Count, "zero cancel failures");
        Assert.Equal(DocStatus.Cancelled, (await Docs.GetAsync(admin, "BulkSubmittableNote", d1.Name)).DocStatus, "d1 is Cancelled");

        // 2. Workflow-driven DocType (PurchaseRequest)
        var andi = U("andi");
        var budi = U("budi");
        var rina = U("rina");
        var pr1 = await Docs.InsertAsync(andi, PR, P(("vendor", "VND-0001"), ("items", Items(2, 1_500_000))));
        var pr2 = await Docs.InsertAsync(andi, PR, P(("vendor", "VND-0001"), ("items", Items(1, 2_000_000))));

        var wfSubmit = await Docs.BulkSubmitAsync(andi, PR, [pr1.Name, pr2.Name]);
        Assert.Equal(2, wfSubmit.Succeeded.Count, "both workflow PRs submitted for approval");
        Assert.Equal("Menunggu Atasan", (await Docs.GetAsync(andi, PR, pr1.Name)).WorkflowState, "pr1 moved to Menunggu Atasan");

        // Approve both as Manager (budi) so they are in Disetujui state, then BulkCancel as Procurement (rina)
        var dPr1 = await Docs.GetAsync(budi, PR, pr1.Name);
        var dPr2 = await Docs.GetAsync(budi, PR, pr2.Name);
        await Docs.RunActionAsync(budi, PR, pr1.Name, "Setujui", dPr1.Version, null);
        await Docs.RunActionAsync(budi, PR, pr2.Name, "Setujui", dPr2.Version, null);

        var wfCancel = await Docs.BulkCancelAsync(rina, PR, [pr1.Name, pr2.Name], "Cancelled in bulk by procurement");
        Assert.Equal(2, wfCancel.Succeeded.Count, "both workflow PRs cancelled");
        Assert.Equal("Dibatalkan", (await Docs.GetAsync(rina, PR, pr1.Name)).WorkflowState, "pr1 moved to Dibatalkan");
        Assert.Equal(DocStatus.Cancelled, (await Docs.GetAsync(rina, PR, pr1.Name)).DocStatus, "pr1 docstatus is Cancelled");
    }

    [Test]
    public Task P1_5_AdminGrid_selection_and_export_xlsx_and_csv_work()
    {
        var prefs = new Framework.Preferences.InMemoryUserPreferenceStore();
        var gridMgr = new Framework.Web.Services.DataGridPreferenceManager<RoleDef>(prefs, null!, "grid:admin:roles", () => { });

        var items = new List<RoleDef>
        {
            new() { Id = "r1", TenantId = "default", Name = "System Manager", Description = "Full system access", IsSystem = true },
            new() { Id = "r2", TenantId = "default", Name = "Purchasing", Description = "=SUM(A1:A2) formula test", IsSystem = false },
            new() { Id = "r3", TenantId = "default", Name = "Finance", Description = "Finance operations", IsSystem = false }
        };

        var cols = new List<Framework.Web.Services.GridExportColumn<RoleDef>>
        {
            new("name", "Role", r => r.Name),
            new("description", "Description", r => r.Description),
            new("type", "Type", r => r.IsSystem ? "System" : "Custom")
        };

        // 1. Export all rows to XLSX
        var xlsxAll = gridMgr.BuildExport("xlsx", "roles", items, cols);
        Assert.Equal(3, xlsxAll.Rows, "xlsx all row count");
        Assert.True(xlsxAll.FileName.EndsWith(".xlsx", StringComparison.Ordinal), "xlsx filename");
        var sheets = SpreadsheetReader.Read(xlsxAll.Content, xlsxAll.FileName, 10 * 1024 * 1024);
        Assert.Equal(4, sheets[0].Rows.Count, "header row + 3 data rows");

        // 2. Select only r2 and r3, and hide 'type' column
        gridMgr.ToggleSelect(items[1], true);
        gridMgr.ToggleSelect(items[2], true);
        Assert.Equal(2, gridMgr.SelectedCount, "2 items selected");
        Assert.True(gridMgr.IsSomeSelected(items), "some selected");
        Assert.True(!gridMgr.IsAllSelected(items), "not all selected");

        gridMgr.Preference.Columns["type"] = new Framework.Preferences.GridColumnPreference { Visible = false };
        var csvSelected = gridMgr.BuildExport("csv", "roles", items, cols);
        Assert.Equal(2, csvSelected.Rows, "csv selected row count");
        var csvText = Encoding.UTF8.GetString(csvSelected.Content).TrimStart('\uFEFF');
        Assert.True(csvText.Contains("Role,Description", StringComparison.Ordinal), "csv headers exclude hidden Type column");
        Assert.True(!csvText.Contains("System Manager", StringComparison.Ordinal), "unselected row excluded");
        Assert.True(csvText.Contains("'=SUM(A1:A2) formula test", StringComparison.Ordinal), "formula injection sanitized in admin grid export");

        // 3. Select all and clear selection
        gridMgr.ToggleSelectAll(items, true);
        Assert.True(gridMgr.IsAllSelected(items), "all selected");
        gridMgr.ClearSelection();
        Assert.Equal(0, gridMgr.SelectedCount, "selection cleared");
        return Task.CompletedTask;
    }
}
