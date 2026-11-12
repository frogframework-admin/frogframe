using Framework.Files;
using Framework.Metadata;
using Framework.Navigation;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Master Modul sebagai referensi DocType.module: wajib, dari master, dan menentukan tampilan menu.</summary>
sealed partial class Tests
{
    [Test]
    public async Task Modul_wajib_dan_harus_ada_di_master_saat_publish()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var scope = sp.GetRequiredService<RoleCatalog>().Scope("acme");
        Assert.True(scope.Modules.Contains("HR") && scope.Modules.Contains("PROCUREMENT"), "kode modul bawaan tersedia di master");

        var tanpa = RoleClone(reg, "Department");
        tanpa.Name = "TestCustomDept";
        tanpa.Module = null;
        var pTanpa = reg.Preview(tanpa, scope);
        Assert.True(!pTanpa.Valid && pTanpa.Errors.Any(e => e.Path == "module" && e.Message.Contains("wajib")), "modul kosong ditolak: " + string.Join("; ", pTanpa.Errors));

        var salah = RoleClone(reg, "Department");
        salah.Name = "TestCustomDept";
        salah.Module = "PENGADAAN_BARU";
        var pSalah = reg.Preview(salah, scope);
        Assert.True(!pSalah.Valid && pSalah.Errors.Any(e => e.Path == "module" && e.Message.Contains("PENGADAAN_BARU")), "modul di luar master ditolak");

        var benar = RoleClone(reg, "Department");
        benar.Name = "TestCustomDept";
        benar.Module = "HR";
        Assert.True(reg.Preview(benar, scope).Valid, "modul dari master diterima");

        // Child table tidak perlu modul: ikut DocType induknya.
        var child = RoleClone(reg, "PurchaseRequestItem");
        child.Name = "TestCustomChild";
        child.Module = null;
        Assert.True(!reg.Preview(child, scope).Errors.Any(e => e.Path == "module"), "child table tanpa modul diterima");
        await Task.CompletedTask;
    }

    [Test]
    public Task Modul_di_resolve_hanya_lewat_kode()
    {
        var mods = NavigationMenuDefaults.CreateDefaultModules("acme", DateTimeOffset.UtcNow);
        Assert.True(NavigationMenuDefaults.ResolveModule(mods, "HR")?.Code == "HR", "kode HR di-resolve");
        Assert.True(NavigationMenuDefaults.ResolveModule(mods, "hr")?.Code == "HR", "kode tidak peka huruf besar");
        Assert.True(NavigationMenuDefaults.ResolveModule(mods, "Pengadaan") is null, "nilai lama tidak lagi dipetakan");
        Assert.True(NavigationMenuDefaults.ResolveModule(mods, "Human Resources") is null, "nama modul tidak lagi dipakai sebagai referensi");
        Assert.True(NavigationMenuDefaults.ResolveModule(mods, null) is null, "kosong → null");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Kode_modul_dinormalkan_dan_tidak_boleh_ganda_per_tenant()
    {
        var store = sp.GetRequiredService<INavigationMenuStore>();
        var baru = new NavigationModule { TenantId = "acme", Name = "Uji Modul", Code = "uji_modul" };
        await store.SaveModuleAsync(baru);
        Assert.True(baru.Code == "UJI_MODUL", "kode dinormalkan ke huruf besar");

        var ganda = new NavigationModule { TenantId = "acme", Name = "Duplikat", Code = "UJI_MODUL" };
        var ex = await Assert.Throws<InvalidOperationException>(() => store.SaveModuleAsync(ganda), "kode ganda ditolak");
        Assert.True(ex.Message.Contains("UJI_MODUL"), ex.Message);

        var buruk = new NavigationModule { TenantId = "acme", Name = "Buruk", Code = "1x" };
        await Assert.Throws<ArgumentException>(() => store.SaveModuleAsync(buruk), "kode tidak valid ditolak");

        await store.DeleteModuleAsync(baru.Id, "acme");
    }
}

/// <summary>Perbaikan A: kode modul kekal, audit metadata melaporkan ketidakcocokan, dan file yatim dibersihkan.</summary>
sealed partial class Tests
{
    [Test]
    public async Task Kode_modul_tidak_bisa_diubah_setelah_tersimpan()
    {
        var store = sp.GetRequiredService<INavigationMenuStore>();
        var awal = new NavigationModule { TenantId = "acme", Name = "Uji Ubah", Code = "UJI_UBAH" };
        await store.SaveModuleAsync(awal);
        // Instance baru (bukan objek yang sama) agar perbandingan dengan data tersimpan benar-benar terjadi.
        var ubah = new NavigationModule { Id = awal.Id, TenantId = "acme", Name = "Uji Ubah", Code = "UJI_BARU" };
        var ex = await Assert.Throws<InvalidOperationException>(() => store.SaveModuleAsync(ubah), "kode tidak boleh diubah");
        Assert.True(ex.Message.Contains("tidak bisa diubah"), ex.Message);
        // Mengubah nama (kode sama) tetap boleh.
        await store.SaveModuleAsync(new NavigationModule { Id = awal.Id, TenantId = "acme", Name = "Uji Ubah Nama", Code = "UJI_UBAH" });
        await store.DeleteModuleAsync(awal.Id, "acme");
    }

    [Test]
    public Task Audit_metadata_melaporkan_modul_yang_tidak_ada_di_master()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var kurang = new MetadataScope(new HashSet<string>(["Employee", "Manager", "Finance", "Procurement", "System Manager"], StringComparer.Ordinal),
                                       new HashSet<string>(["HR"], StringComparer.Ordinal));
        var issues = reg.AuditAgainst(kurang);
        Assert.True(issues.Any(i => i.Message.Contains("'PROCUREMENT'")), "modul yang hilang dari master dilaporkan: " + string.Join("; ", issues.Select(i => i.Message)));
        Assert.True(reg.AuditAgainst(sp.GetRequiredService<RoleCatalog>().Scope("acme")).Count == 0, "tenant acme bersih dari audit");
        return Task.CompletedTask;
    }

    [Test]
    public async Task File_yatim_dibersihkan_dan_file_yang_tertaut_tidak()
    {
        var files = sp.GetRequiredService<IFileStore>();
        var lama = DateTimeOffset.UtcNow.AddDays(-2);
        FileRecord Rec(string id, string? docType) => new()
        {
            Id = id, TenantId = "acme", UploadedBy = "andi", ContentType = "image/png", Size = 4,
            Sha256 = "x", CreatedAt = lama, DocType = docType, DocName = docType is null ? null : "PR-1", Field = docType is null ? null : "photo"
        };
        await files.SaveAsync(Rec("yatim_uji_1", null), [1, 2, 3, 4]);
        await files.SaveAsync(Rec("tertaut_uji_1", "PurchaseRequest"), [1, 2, 3, 4]);

        var dihapus = await files.PruneOrphansAsync(DateTimeOffset.UtcNow.AddHours(-24));
        Assert.True(dihapus >= 1, $"file yatim lama dihapus ({dihapus})");
        Assert.True(await files.GetInfoAsync("acme", "yatim_uji_1") is null, "yatim sudah hilang");
        Assert.True(await files.GetInfoAsync("acme", "tertaut_uji_1") is not null, "file yang tertaut tetap ada");
    }
}
