using Framework.Documents;
using Framework.Metadata;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Pengujian Single DocType (Singleton Settings per tenant).</summary>
sealed partial class Tests
{
    [Test]
    public async Task Single_doctype_GetSingleAsync_auto_initializes_with_defaults()
    {
        var admin = U("admin");
        // AccountingSettings adalah Single DocType bawaan
        var settings = await Docs.GetSingleAsync(admin, "AccountingSettings");
        Assert.True(settings is not null, "settings tidak null");
        Assert.Equal("AccountingSettings", settings!.Name, "nama = nama DocType");
        Assert.Equal("AccountingSettings", settings.DocType, "doctype sesuai");
        Assert.Equal(admin.TenantId, settings.TenantId, "tenant sesuai");
    }

    [Test]
    public async Task Single_doctype_SetSingleAsync_inserts_then_updates()
    {
        await SeedLedgerAsync();
        var admin = U("admin");
        var saved1 = await Docs.SetSingleAsync(admin, "AccountingSettings", new Dictionary<string, object?>
        {
            ["default_income_account"] = "4000"
        });

        Assert.Equal("AccountingSettings", saved1.Name, "nama = nama DocType");
        Assert.True(saved1.Version >= 1, "versi tersimpan");

        // SetSingleAsync kedua kali harus update versi
        var saved2 = await Docs.SetSingleAsync(admin, "AccountingSettings", new Dictionary<string, object?>
        {
            ["default_expense_account"] = "5000"
        });

        Assert.Equal("AccountingSettings", saved2.Name, "nama = nama DocType");
        Assert.True(saved2.Version > saved1.Version, "versi naik setelah update");

        var loaded = await Docs.GetSingleAsync(admin, "AccountingSettings");
        Assert.Equal("5000", (string)loaded.Data["default_expense_account"]!, "nilai terupdate");
    }

    [Test]
    public async Task Single_doctype_compiler_rejects_workflow_and_child_table()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var scope = sp.GetRequiredService<RoleCatalog>().Scope("acme");

        // 1. Single DocType dengan Workflow ditolak
        var singleWithWf = RoleClone(reg, "AccountingSettings");
        singleWithWf.Version++;
        singleWithWf.Workflow = "VendorReview";
        var previewWf = reg.Preview(singleWithWf, scope);
        Assert.True(!previewWf.Valid && previewWf.Errors.Any(e => e.Path == "workflow"), "Single DocType dengan workflow ditolak");

        // 2. Child table sebagai Single DocType ditolak
        var childSingle = RoleClone(reg, "JournalEntryItem");
        childSingle.Version++;
        childSingle.IsSingle = true;
        var previewChild = reg.Preview(childSingle, scope);
        Assert.True(!previewChild.Valid && previewChild.Errors.Any(e => e.Path == "isSingle"), "Child table sebagai Single ditolak");
    }

    [Test]
    public async Task Single_doctype_tenant_isolation()
    {
        await SeedLedgerAsync(); // creates 4000 in acme
        var acmeAdmin = U("admin"); // Tenant acme
        var globexAdmin = new UserContext("admin", "Admin Globex", "globex", new HashSet<string> { "System Manager", "Administrator" }, new Dictionary<string, string>());

        // Buat akun 4000 & 4100 di tenant globex
        await Docs.InsertAsync(globexAdmin, "Account", P(("code", "4000"), ("account_name", "Pendapatan Globex"), ("root_type", "Pendapatan"),
            ("balance_type", "Kredit"), ("is_group", false), ("is_active", true)));
        await Docs.InsertAsync(globexAdmin, "Account", P(("code", "4100"), ("account_name", "Pendapatan Lain Globex"), ("root_type", "Pendapatan"),
            ("balance_type", "Kredit"), ("is_group", false), ("is_active", true)));

        await Docs.SetSingleAsync(acmeAdmin, "AccountingSettings", new Dictionary<string, object?>
        {
            ["default_income_account"] = "4000"
        });

        await Docs.SetSingleAsync(globexAdmin, "AccountingSettings", new Dictionary<string, object?>
        {
            ["default_income_account"] = "4100"
        });

        var acmeDoc = await Docs.GetSingleAsync(acmeAdmin, "AccountingSettings");
        var globexDoc = await Docs.GetSingleAsync(globexAdmin, "AccountingSettings");

        Assert.Equal("4000", (string)acmeDoc.Data["default_income_account"]!, "acme terisolasi");
        Assert.Equal("4100", (string)globexDoc.Data["default_income_account"]!, "globex terisolasi");
    }

    [Test]
    public async Task Single_doctype_non_admin_read_and_write_permissions()
    {
        await SeedLedgerAsync();
        var employee = U("andi"); // Role: Employee -> has read on AccountingSettings
        var finance = U("dewi");  // Role: Finance -> has read & write on AccountingSettings

        // 1. Employee with read permission can get single defaults without needing Create permission
        var employeeLoaded = await Docs.GetAsync(employee, "AccountingSettings", "AccountingSettings");
        Assert.Equal("AccountingSettings", employeeLoaded.Name, "Employee can read single default");

        // 2. Employee cannot write
        await Assert.Throws<PermissionDeniedException>(async () =>
        {
            await Docs.SetSingleAsync(employee, "AccountingSettings", new Dictionary<string, object?> { ["default_expense_account"] = "5000" });
        }, "Employee without write permission should be rejected");

        // 3. Finance with write permission can save
        var saved = await Docs.SetSingleAsync(finance, "AccountingSettings", new Dictionary<string, object?>
        {
            ["default_expense_account"] = "5000"
        });
        Assert.Equal("5000", (string)saved.Data["default_expense_account"]!, "Finance can save single doctype");

        // 4. Employee can read updated value
        var readAfter = await Docs.GetAsync(employee, "AccountingSettings", "AccountingSettings");
        Assert.Equal("5000", (string)readAfter.Data["default_expense_account"]!, "Employee can read updated single doctype");
    }
}
