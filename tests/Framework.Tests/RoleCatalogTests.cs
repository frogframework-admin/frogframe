using Framework.Metadata;
using Framework.Security;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Role master sebagai satu sumber kebenaran: publish menolak role tak dikenal, rename/hapus role yang dipakai ditolak.</summary>
sealed partial class Tests
{
    // Klon dengan versi dinaikkan: validasi versi berjalan sebelum pemeriksaan role.
    private static DocTypeDef RoleClone(MetadataRegistry reg, string name)
    {
        var d = MetadataJson.ParseDocType(MetadataJson.Serialize(reg.Current.Get(name).Def));
        d.Version = reg.Current.Get(name).Def.Version + 1;
        return d;
    }

    [Test]
    public async Task Role_permission_tak_dikenal_ditolak_saat_publish_dan_diterima_bila_ada_di_master()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var scope = sp.GetRequiredService<RoleCatalog>().Scope("acme");

        var bad = RoleClone(reg, "Department");
        bad.Permissions.Add(new PermissionDef { Role = "Akuntan", Actions = ["read"], Scope = "all" });
        var rejected = reg.Preview(bad, scope);
        Assert.True(!rejected.Valid && rejected.Errors.Any(e => e.Message.Contains("'Akuntan'") && e.Message.Contains("master role")),
            "role di luar master ditolak: " + string.Join("; ", rejected.Errors));

        var ok = RoleClone(reg, "Department");
        ok.Permissions.Add(new PermissionDef { Role = "Manager", Actions = ["read"], Scope = "all" });
        var okPreview = reg.Preview(ok, scope);
        Assert.True(okPreview.Valid, "role yang ada di master diterima: " + string.Join("; ", okPreview.Errors));

        // Tanpa daftar master (startup), pemeriksaan tidak dijalankan: perilaku lama dipertahankan.
        Assert.True(reg.Preview(bad).Valid, "tanpa daftar master tidak ada pemeriksaan role");
    }

    [Test]
    public Task Role_transisi_workflow_tak_dikenal_ditolak()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var wf = MetadataJson.ParseWorkflow(MetadataJson.Serialize(reg.Workflows.First()));
        wf.Version = wf.Version + 1;
        wf.Transitions.First().AllowedRoles = ["Procurment"]; // salah ketik
        var preview = reg.PreviewWorkflow(wf, false, sp.GetRequiredService<RoleCatalog>().Scope("acme"));
        Assert.True(!preview.Valid && preview.Errors.Any(e => e.Message.Contains("'Procurment'")), "salah ketik role transisi tertangkap");
        return Task.CompletedTask;
    }

    [Test]
    public async Task Role_yang_dipakai_metadata_atau_akun_tidak_bisa_di_rename_atau_dihapus()
    {
        var reg = sp.GetRequiredService<MetadataRegistry>();
        var catalog = sp.GetRequiredService<RoleCatalog>();
        var blockedDoc = await catalog.BlockingUseAsync("acme", "Manager", reg.Current);
        Assert.True(blockedDoc is not null && blockedDoc.Contains("izin DocType"), "Manager dipakai izin DocType: " + blockedDoc);
        var blockedWf = await catalog.BlockingUseAsync("acme", "Finance", reg.Current);
        Assert.True(blockedWf is not null, "Finance dipakai izin/transisi: " + blockedWf);
        Assert.True(await catalog.BlockingUseAsync("acme", "RoleTidakDipakai", reg.Current) is null, "role tidak dipakai boleh dihapus");
        Assert.True((await catalog.NamesAsync("acme")).Contains("Procurement"), "master role tenant tersedia untuk editor");
    }
}
