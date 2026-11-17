using System.Text.Json;
using Framework.Documents;
using Framework.Persistence;
using Framework.Preferences;
using Framework.Security;

/// <summary>
/// Tests for P1-8: Saved Filters / Custom Views per DocType persisted via IUserPreferenceStore.
/// </summary>
sealed partial class Tests
{
    [Test]
    public async Task P1_8_CustomViews_Crud_Defaults_And_Isolation()
    {
        var store = new InMemoryUserPreferenceStore();

        var v1 = new SavedCustomView
        {
            Id = "view-po-pending",
            Name = "PO Tertunda Bulan Ini",
            Search = "Logistik",
            Filters =
            [
                new SavedFilterClause { Field = "workflow_state", Op = "=", Value = "Draft" },
                new SavedFilterClause { Field = "total", Op = ">", Value = 5000000m }
            ],
            SortField = "modified",
            SortDirection = "desc",
            IsDefault = true
        };

        var v2 = new SavedCustomView
        {
            Id = "view-po-approved",
            Name = "Semua PO Disetujui",
            Filters =
            [
                new SavedFilterClause { Field = "workflow_state", Op = "=", Value = "Approved" }
            ],
            IsDefault = false
        };

        // 1. Simpan view v1 untuk user1 pada tenant acme di DocType PurchaseRequest
        await store.SaveCustomViewAsync("acme", "user1", "PurchaseRequest", v1);

        var pref = await store.GetCustomViewsAsync("acme", "user1", "PurchaseRequest");
        Assert.Equal(1, pref.Views.Count, "1 view tersimpan");
        Assert.Equal(v1.Id, pref.DefaultViewId, "default view ID sesuai v1");
        Assert.True(pref.Views[0].IsDefault, "v1 ditandai sebagai default");
        Assert.Equal(2, pref.Views[0].Filters.Count, "2 klausul filter pada v1");
        Assert.Equal("Draft", pref.Views[0].Filters[0].Value, "filter workflow_state Draft");

        // 2. Simpan view v2
        await store.SaveCustomViewAsync("acme", "user1", "PurchaseRequest", v2);

        pref = await store.GetCustomViewsAsync("acme", "user1", "PurchaseRequest");
        Assert.Equal(2, pref.Views.Count, "2 views tersimpan");
        Assert.Equal(v1.Id, pref.DefaultViewId, "default view tetap v1");

        // 3. Set v2 sebagai default view
        await store.SetDefaultCustomViewAsync("acme", "user1", "PurchaseRequest", v2.Id);

        pref = await store.GetCustomViewsAsync("acme", "user1", "PurchaseRequest");
        Assert.Equal(v2.Id, pref.DefaultViewId, "default view kini v2");
        Assert.True(pref.Views.First(v => v.Id == v2.Id).IsDefault, "v2 IsDefault true");
        Assert.True(!pref.Views.First(v => v.Id == v1.Id).IsDefault, "v1 IsDefault false");

        // 4. Update nama dan filter v1 (upsert by id)
        var v1Updated = v1 with
        {
            Name = "PO Tertunda Diperbarui",
            Filters = [new SavedFilterClause { Field = "workflow_state", Op = "=", Value = "Draft" }],
            IsDefault = false
        };
        await store.SaveCustomViewAsync("acme", "user1", "PurchaseRequest", v1Updated);

        pref = await store.GetCustomViewsAsync("acme", "user1", "PurchaseRequest");
        Assert.Equal(2, pref.Views.Count, "jumlah view tidak bertambah saat update");
        var reloadedV1 = pref.Views.First(v => v.Id == v1.Id);
        Assert.Equal("PO Tertunda Diperbarui", reloadedV1.Name, "nama view terupdate");
        Assert.Equal(1, reloadedV1.Filters.Count, "filter v1 terupdate");
        Assert.Equal(v2.Id, pref.DefaultViewId, "default view tetap v2");

        // 5. Isolasi Pengguna: user2 tidak boleh melihat view user1
        var user2Views = await store.GetCustomViewsAsync("acme", "user2", "PurchaseRequest");
        Assert.Equal(0, user2Views.Views.Count, "user2 tidak melihat view user1");
        Assert.True(string.IsNullOrEmpty(user2Views.DefaultViewId), "user2 tidak punya default view");

        // 6. Isolasi Tenant: user1 pada tenant2 tidak boleh melihat view tenant acme
        var tenant2Views = await store.GetCustomViewsAsync("other_tenant", "user1", "PurchaseRequest");
        Assert.Equal(0, tenant2Views.Views.Count, "tenant lain tidak melihat view");

        // 7. Isolasi DocType: DocType lain (mis. SalesInvoice) tidak terpengaruh
        var invoiceViews = await store.GetCustomViewsAsync("acme", "user1", "SalesInvoice");
        Assert.Equal(0, invoiceViews.Views.Count, "DocType lain tidak memiliki view ini");

        // 8. Hapus view default (v2)
        await store.DeleteCustomViewAsync("acme", "user1", "PurchaseRequest", v2.Id);

        pref = await store.GetCustomViewsAsync("acme", "user1", "PurchaseRequest");
        Assert.Equal(1, pref.Views.Count, "tersisa 1 view setelah penghapusan");
        Assert.True(string.IsNullOrEmpty(pref.DefaultViewId), "default view di-clear karena view default dihapus");
    }

    [Test]
    public Task P1_8_CustomViews_JsonSerialization_And_Unwrap()
    {
        var original = new DocTypeCustomViewsPreference
        {
            Views =
            [
                new SavedCustomView
                {
                    Id = "v-test",
                    Name = "Filter Campuran",
                    Search = "query-text",
                    Filters =
                    [
                        new SavedFilterClause { Field = "vendor_name", Op = "like", Value = "PT Maju" },
                        new SavedFilterClause { Field = "total", Op = ">", Value = 1500000m },
                        new SavedFilterClause { Field = "is_active", Op = "=", Value = true },
                        new SavedFilterClause { Field = "tags", Op = "in", Value = new List<object?> { "A", "B", "C" } }
                    ]
                }
            ],
            DefaultViewId = "v-test"
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<DocTypeCustomViewsPreference>(json);

        Assert.True(restored is not null, "deserialisasi tidak null");
        Assert.Equal(1, restored!.Views.Count, "1 view direstorasi");
        var view = restored.Views[0];
        Assert.Equal("Filter Campuran", view.Name, "nama view");

        // Test ToFilterClause() unwrapping
        var clauses = view.Filters.Select(f => f.ToFilterClause()).ToList();
        Assert.Equal(4, clauses.Count, "4 filter clause");

        Assert.Equal("vendor_name", clauses[0].Field, "field 0");
        Assert.Equal("PT Maju", clauses[0].Value, "string unwrapped");

        Assert.Equal("total", clauses[1].Field, "field 1");
        Assert.True(clauses[1].Value is decimal or long, "number unwrapped to numeric type");

        Assert.Equal("is_active", clauses[2].Field, "field 2");
        Assert.Equal(true, clauses[2].Value, "boolean unwrapped");

        Assert.Equal("tags", clauses[3].Field, "field 3");
        Assert.True(clauses[3].Value is System.Collections.IEnumerable, "array unwrapped to list");
        return Task.CompletedTask;
    }

    [Test]
    public async Task P1_8_CustomViews_DocumentService_Filtering_And_Export()
    {
        var admin = U("admin");
        var marker = "CVFilterTest_" + Guid.NewGuid().ToString("N")[..8];

        var v1 = await Docs.InsertAsync(admin, "Vendor", P(
            ("vendor_name", $"{marker} Alpha Hardware"),
            ("category", "Perangkat keras"),
            ("npwp", "111111111111000")));

        var v2 = await Docs.InsertAsync(admin, "Vendor", P(
            ("vendor_name", $"{marker} Beta Services"),
            ("category", "Jasa"),
            ("npwp", "222222222222000")));

        var v3 = await Docs.InsertAsync(admin, "Vendor", P(
            ("vendor_name", $"{marker} Gamma Hardware"),
            ("category", "Perangkat keras"),
            ("npwp", "333333333333000")));

        // Buat filter preset seperti yang disimpan di SavedCustomView: category = "Perangkat keras" AND vendor_name like marker
        var savedFilter = new SavedCustomView
        {
            Name = "Hardware Vendors",
            Filters =
            [
                new SavedFilterClause { Field = "category", Op = "=", Value = "Perangkat keras" },
                new SavedFilterClause { Field = "vendor_name", Op = "like", Value = marker }
            ]
        };

        var clauses = savedFilter.Filters.Select(f => f.ToFilterClause()).ToList();

        // 1. Query DocumentService.ListAsync dengan klausul dari saved filter (allowAllReadableFields = true)
        var listRes = await Docs.ListAsync(admin, "Vendor", new ListRequest(Filters: clauses), allowAllReadableFields: true);
        Assert.Equal(2, listRes.Total, "Hanya 2 vendor perangkat keras yang sesuai filter");
        var names = listRes.Items.Select(r => (string)r["name"]!).ToHashSet();
        Assert.True(names.Contains(v1.Name), "v1 masuk list");
        Assert.True(!names.Contains(v2.Name), "v2 tidak masuk list (jasa)");
        Assert.True(names.Contains(v3.Name), "v3 masuk list");

        // 2. Query ExportListAsync dengan klausul yang sama
        var exportRes = await Docs.ExportListAsync(
            admin,
            "Vendor",
            "csv",
            new ListRequest(Filters: clauses),
            visibleColumns: ["vendor_name", "category"]);

        Assert.Equal(2, exportRes.Rows, "Export CSV hanya mencakup 2 baris yang sesuai filter");
        var csvStr = System.Text.Encoding.UTF8.GetString(exportRes.Content);
        Assert.True(csvStr.Contains("Alpha Hardware", StringComparison.Ordinal), "csv memuat Alpha Hardware");
        Assert.True(!csvStr.Contains("Beta Services", StringComparison.Ordinal), "csv tidak memuat Beta Services");
        Assert.True(csvStr.Contains("Gamma Hardware", StringComparison.Ordinal), "csv memuat Gamma Hardware");
    }
}
