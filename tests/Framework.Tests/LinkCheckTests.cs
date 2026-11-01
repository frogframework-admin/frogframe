using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Persistence.PostgreSql;
using Microsoft.Extensions.DependencyInjection;

sealed partial class Tests
{
    [Test]
    public async Task LinkCheck_Delete_dokumen_gagal_bila_ditautkan_oleh_dokumen_lain()
    {
        // 1. Buat vendor baru sebagai admin
        var vendor = await Docs.InsertAsync(U("admin"), "Vendor", P(("vendor_name", "PT Link Check Direct")));

        // 2. Buat PurchaseRequest yang menautkan vendor ini
        var pr = await Docs.InsertAsync(U("andi"), PR, P(
            ("vendor", vendor.Name),
            ("items", Items(1, 50000)),
            ("request_date", "2026-11-02"),
            ("required_by", "2026-11-05")));

        // 3. Upaya menghapus vendor harus ditolak dengan LinkedDocumentException
        var ex = await Assert.Throws<LinkedDocumentException>(
            () => Docs.DeleteAsync(U("admin"), "Vendor", vendor.Name, vendor.Version),
            "vendor ditautkan oleh PurchaseRequest");

        Assert.True(ex.Message.Contains("PurchaseRequest"), "pesan error menyebutkan dokumen penaut");
        Assert.True(ex.Message.Contains(pr.Name), "pesan error menyebutkan nomor dokumen penaut");

        // 4. Batalkan / hapus PurchaseRequest penaut
        await Docs.DeleteAsync(U("andi"), PR, pr.Name, pr.Version);

        // 5. Sekarang vendor dapat dihapus dengan sukses
        await Docs.DeleteAsync(U("admin"), "Vendor", vendor.Name, vendor.Version);
        var deleted = await Docs.QueryForReportAsync(U("admin"), "Vendor", [new FilterClause("name", "=", vendor.Name)], DocActions.Read);
        Assert.Equal(0, deleted.Count, "vendor berhasil dihapus setelah dokumen penaut dibersihkan");
    }

    [Test]
    public async Task LinkCheck_Delete_dokumen_gagal_bila_ditautkan_di_child_table()
    {
        // 1. Buat produk baru sebagai admin
        var product = await Docs.InsertAsync(U("admin"), "Product", P(
            ("product_name", "Komputer Link Check"),
            ("uom", "unit"),
            ("price", 15000000m)));

        // 2. Buat PurchaseRequest yang menautkan produk ini di child table items (total > 10jt butuh justification)
        var itemRows = new List<Dictionary<string, object?>>
        {
            new() { ["product"] = product.Name, ["item_name"] = "Komputer", ["qty"] = 2m, ["uom"] = "unit", ["rate"] = 15000000m }
        };
        var pr = await Docs.InsertAsync(U("andi"), PR, P(
            ("items", itemRows),
            ("request_date", "2026-11-02"),
            ("required_by", "2026-11-05"),
            ("justification", "Pengadaan komputer link check")));

        // 3. Upaya menghapus produk harus ditolak karena dipakai di child table
        var ex = await Assert.Throws<LinkedDocumentException>(
            () => Docs.DeleteAsync(U("admin"), "Product", product.Name, product.Version),
            "produk ditautkan di child table PurchaseRequestItem");

        Assert.True(ex.Message.Contains("PurchaseRequest"), "pesan error menyebutkan PurchaseRequest");
        Assert.True(ex.Message.Contains(pr.Name), "pesan error menyebutkan nomor PR penaut");

        // 4. Bersihkan PR lalu hapus produk
        await Docs.DeleteAsync(U("andi"), PR, pr.Name, pr.Version);
        await Docs.DeleteAsync(U("admin"), "Product", product.Name, product.Version);
    }

    [Test]
    public Task LinkCheck_IgnoreLinks_diabaikan_dari_daftar_tautan()
    {
        var targetDt = new DocTypeDef { Name = "TargetDoc", Title = "Target", Fields = [new FieldDef { Name = "title", Type = "Data" }] };
        var sourceDt = new DocTypeDef
        {
            Name = "SourceDoc",
            Title = "Source",
            Fields =
            [
                new FieldDef { Name = "target_ignored", Type = "Link", LinkTo = "TargetDoc", IgnoreLinks = true },
                new FieldDef { Name = "target_tracked", Type = "Link", LinkTo = "TargetDoc", IgnoreLinks = false }
            ]
        };

        var compiler = sp.GetRequiredService<MetadataCompiler>();
        var snap = compiler.Compile([targetDt, sourceDt], []);

        Assert.True(snap.IncomingLinks.ContainsKey("TargetDoc"), "TargetDoc memiliki incoming links");
        var links = snap.IncomingLinks["TargetDoc"];
        Assert.Equal(1, links.Count, "hanya 1 link yang terdaftar karena 1 lainnya IgnoreLinks");
        Assert.Equal("target_tracked", links[0].FieldName, "field yang terdaftar adalah target_tracked");

        return Task.CompletedTask;
    }

    [Test, PgTest]
    public async Task Pg_LinkCheck_PostgresDocumentStore_mendeteksi_tautan_langsung_dan_child_table()
    {
        var store = (PostgresDocumentStore)sp.GetRequiredService<IDocumentStore>();
        var tenantId = "acme";

        // Tautan langsung (top-level)
        var linkDefsDirect = new List<DocTypeLinkDef>
        {
            new("PurchaseRequest", "vendor", "Vendor", false, null)
        };

        // Buat vendor dan PR baru di database
        var vendor = await Docs.InsertAsync(U("admin"), "Vendor", P(("vendor_name", "Vendor Pg Test")));

        var pr = await Docs.InsertAsync(U("andi"), PR, P(
            ("vendor", vendor.Name),
            ("items", Items(1, 10000)),
            ("request_date", "2026-11-02"),
            ("required_by", "2026-11-05")));

        var foundDirect = await store.FindFirstLinkAsync(tenantId, "Vendor", vendor.Name, linkDefsDirect);
        Assert.True(foundDirect is not null, "PostgresDocumentStore mendeteksi tautan vendor via GIN @>");
        Assert.Equal("PurchaseRequest", foundDirect!.LinkingDocType, "LinkingDocType adalah PurchaseRequest");
        Assert.Equal(pr.Name, foundDirect.LinkingDocName, "LinkingDocName sesuai nomor PR");

        // Tautan child table (nested array of jsonb objects)
        var product = await Docs.InsertAsync(U("admin"), "Product", P(
            ("product_name", "Produk Pg Test"),
            ("uom", "unit"),
            ("price", 10000m)));

        var pr2 = await Docs.InsertAsync(U("andi"), PR, P(
            ("items", new List<Dictionary<string, object?>>
            {
                new() { ["product"] = product.Name, ["item_name"] = "Item Pg", ["qty"] = 1m, ["uom"] = "unit", ["rate"] = 10000m }
            }),
            ("request_date", "2026-11-02"),
            ("required_by", "2026-11-05")));

        var linkDefsChild = new List<DocTypeLinkDef>
        {
            new("PurchaseRequest", "product", "Produk", true, "items")
        };

        var foundChild = await store.FindFirstLinkAsync(tenantId, "Product", product.Name, linkDefsChild);
        Assert.True(foundChild is not null, "PostgresDocumentStore mendeteksi tautan child table via GIN @>");
        Assert.Equal("PurchaseRequest", foundChild!.LinkingDocType, "LinkingDocType child table adalah PurchaseRequest");
        Assert.Equal(pr2.Name, foundChild.LinkingDocName, "LinkingDocName child table sesuai nomor PR");

        // Cleanup
        await Docs.DeleteAsync(U("andi"), PR, pr.Name, pr.Version);
        await Docs.DeleteAsync(U("andi"), PR, pr2.Name, pr2.Version);
        await Docs.DeleteAsync(U("admin"), "Vendor", vendor.Name, vendor.Version);
        await Docs.DeleteAsync(U("admin"), "Product", product.Name, product.Version);
    }
}
