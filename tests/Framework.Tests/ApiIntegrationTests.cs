using System.Net;
using System.Text.Json;
using Apps.Sample.Domain;
using Framework.Api;
using Framework.Documents;
using Framework.Persistence;
using Framework.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Kunci API integrasi & method kustom.</summary>
sealed partial class Tests
{
    private ApiKeyService ApiKeys => sp.GetRequiredService<ApiKeyService>();

    [Test]
    public async Task ApiKey_hanya_hash_tersimpan_dan_token_diverifikasi_sebagai_akunnya()
    {
        var (key, token) = await ApiKeys.CreateAsync(U("admin"), new CreateApiKeyRequest("ERP keuangan", "dewi", ["doc:PurchaseRequest:read", "method:*"], [], null));
        Assert.True(token.StartsWith($"dfk_{key.Id}.") && token.Length > 50, "format token dfk_<id>.<secret>");
        var stored = (await sp.GetRequiredService<IApiKeyStore>().GetAsync(key.Id))!;
        var secret = token.Split('.', 2)[1];
        Assert.True(!JsonSerializer.Serialize(stored).Contains(secret), "secret tidak pernah disimpan, hanya hash");
        var ok = await ApiKeys.VerifyAsync(token, IPAddress.Loopback);
        Assert.True(ok is not null && ok.Account.Id == "dewi" && ok.Account.TenantId == "acme", "token valid → akun pemilik");
        Assert.True(await ApiKeys.VerifyAsync(token[..^2] + "xx", IPAddress.Loopback) is null, "secret salah ditolak");
        Assert.True(await ApiKeys.VerifyAsync("dfk_tidakada.abcdefghijklmnopqrstuvwxyz", IPAddress.Loopback) is null, "kunci tidak dikenal ditolak");
        Assert.True(await ApiKeys.VerifyAsync("Bearer apapun", IPAddress.Loopback) is null, "format lain ditolak");
        Assert.True((await sp.GetRequiredService<IApiKeyStore>().GetAsync(key.Id))!.LastUsedAt is not null, "waktu pemakaian terakhir dicatat");
        await Assert.Throws<PermissionDeniedException>(() => ApiKeys.CreateAsync(U("dewi"), new CreateApiKeyRequest("x", "dewi", [], [], null)), "hanya System Manager membuat kunci");
        await Assert.Throws<ApiKeyException>(() => ApiKeys.CreateAsync(U("admin"), new CreateApiKeyRequest("x", "tono", [], [], null)), "akun tenant lain ditolak");
        await Assert.Throws<ApiKeyException>(() => ApiKeys.CreateAsync(U("admin"), new CreateApiKeyRequest("x", "dewi", ["doc:purchase:read; DROP"], [], null)), "cakupan tidak valid ditolak");
    }

    [Test]
    public async Task ApiKey_dicabut_kedaluwarsa_ip_dan_akun_nonaktif_ditolak()
    {
        var admin = U("admin");
        var (k1, t1) = await ApiKeys.CreateAsync(admin, new CreateApiKeyRequest("Cabut", "dewi", [], [], null));
        await ApiKeys.RevokeAsync(admin, k1.Id);
        Assert.True(await ApiKeys.VerifyAsync(t1, IPAddress.Loopback) is null, "kunci dicabut langsung ditolak");

        var (_, t2) = await ApiKeys.CreateAsync(admin, new CreateApiKeyRequest("IP", "dewi", [], ["10.0.5.0/24", "203.0.113.7"], null));
        Assert.True(await ApiKeys.VerifyAsync(t2, IPAddress.Parse("10.0.5.42")) is not null, "IP dalam CIDR diterima");
        Assert.True(await ApiKeys.VerifyAsync(t2, IPAddress.Parse("203.0.113.7").MapToIPv6()) is not null, "IPv4 yang dipetakan ke IPv6 dikenali");
        Assert.True(await ApiKeys.VerifyAsync(t2, IPAddress.Parse("10.0.6.1")) is null, "IP di luar daftar ditolak");

        await Assert.Throws<ApiKeyException>(() => ApiKeys.CreateAsync(admin, new CreateApiKeyRequest("Lampau", "dewi", [], [], DateTimeOffset.UtcNow.AddDays(-1))), "kedaluwarsa di masa lalu ditolak");
        var store = sp.GetRequiredService<IApiKeyStore>();
        var (k3, t3) = await ApiKeys.CreateAsync(admin, new CreateApiKeyRequest("Pendek", "dewi", [], [], DateTimeOffset.UtcNow.AddMinutes(5)));
        var stored = (await store.GetAsync(k3.Id))!; stored.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1); await store.SaveAsync(stored);
        Assert.True(await ApiKeys.VerifyAsync(t3, IPAddress.Loopback) is null, "kunci kedaluwarsa ditolak");

        var (_, t4) = await ApiKeys.CreateAsync(admin, new CreateApiKeyRequest("Akun", "sari", [], [], null));
        var users = sp.CreateScope().ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var sari = (await users.FindByIdAsync("sari"))!; sari.IsActive = false; await users.UpdateAsync(sari);
        Assert.True(await ApiKeys.VerifyAsync(t4, IPAddress.Loopback) is null, "akun dinonaktifkan → kuncinya ikut tidak berlaku");
    }

    [Test]
    public Task ApiKey_cakupan_memakai_wildcard_per_segmen()
    {
        string[] g = ["doc:PurchaseRequest:read", "doc:Vendor:*", "doc:*:list", "method:sisa-anggaran"];
        Assert.True(ApiScopes.Allows(g, "doc:PurchaseRequest:read") && ApiScopes.Allows(g, "doc:Vendor:delete") && ApiScopes.Allows(g, "doc:Employee:list"), "cakupan cocok");
        Assert.True(!ApiScopes.Allows(g, "doc:PurchaseRequest:update") && !ApiScopes.Allows(g, "doc:Employee:read") && !ApiScopes.Allows(g, "method:ajukan-massal"), "di luar cakupan ditolak");
        Assert.True(ApiScopes.Allows(["*"], "method:apa-saja") && !ApiScopes.Allows([], "doc:Vendor:read"), "'*' semua; kosong tidak ada");
        Assert.True(ApiScopes.IsValid("doc:*:list") && ApiScopes.IsValid("report:rt_abc") && !ApiScopes.IsValid("doc:Vendor:hapus") && !ApiScopes.IsValid("method:Huruf_Besar"), "validasi format cakupan");
        return Task.CompletedTask;
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Test]
    public async Task ApiMethod_argumen_divalidasi_ketat_dan_role_ditegakkan()
    {
        var reg = sp.GetRequiredService<ApiMethodRegistry>();
        Assert.True(reg.Visible(U("dewi")).Any(m => m.Name == "sisa-anggaran") && reg.Visible(U("andi")).All(m => m.Name != "sisa-anggaran"), "daftar method mengikuti role");
        await Assert.Throws<PermissionDeniedException>(() => reg.InvokeAsync(U("andi"), "sisa-anggaran", Json("""{"departemen":"IT"}"""), sp), "role tanpa izin ditolak");
        await Assert.Throws<KeyNotFoundException>(() => reg.InvokeAsync(U("dewi"), "tidak-ada", null, sp), "method tidak dikenal");
        var miss = await Assert.Throws<ValidationFailedException>(() => reg.InvokeAsync(U("dewi"), "sisa-anggaran", Json("{}"), sp), "argumen wajib");
        Assert.True(miss.Errors.Any(e => e.Path == "departemen"), "argumen wajib disebut");
        var extra = await Assert.Throws<ValidationFailedException>(() => reg.InvokeAsync(U("dewi"), "sisa-anggaran", Json("""{"departemen":"IT","owner":"admin"}"""), sp), "argumen asing");
        Assert.True(extra.Errors.Any(e => e.Path == "owner" && e.Message.Contains("tidak dikenal")), "argumen yang tidak dideklarasikan ditolak (mass assignment)");
        var type = await Assert.Throws<ValidationFailedException>(() => reg.InvokeAsync(U("dewi"), "sisa-anggaran", Json("""{"departemen":"IT","tahun":"dua ribu"}"""), sp), "tipe salah");
        Assert.True(type.Errors.Any(e => e.Path == "tahun" && e.Message.Contains("angka")), "pesan tipe jelas");
        await Assert.Throws<ApiMethodException>(() => reg.InvokeAsync(U("dewi"), "sisa-anggaran", Json("""{"departemen":"IT","tahun":1800}"""), sp), "validasi milik method");
    }

    [Test]
    public async Task ApiMethod_sisa_anggaran_mengikuti_hak_akses_pemanggil()
    {
        var reg = sp.GetRequiredService<ApiMethodRegistry>();
        async Task<JsonElement> Call(string user, string dept) =>
            Json(JsonSerializer.Serialize(await reg.InvokeAsync(U(user), "sisa-anggaran", Json($$"""{"departemen":"{{dept}}","tahun":{{DateTime.Today.Year}}}"""), sp)));
        var finance = await Call("dewi", "IT");
        Assert.True(finance.GetProperty("jumlahPengajuan").GetInt32() > 0 && finance.GetProperty("terpakai").GetDecimal() > 0, "Finance melihat pengajuan IT: " + finance);
        Assert.Equal(finance.GetProperty("anggaran").GetDecimal() - finance.GetProperty("terpakai").GetDecimal(), finance.GetProperty("sisa").GetDecimal(), "sisa = anggaran - terpakai");
        var otherManager = await Call("maya", "IT");
        Assert.Equal(0, otherManager.GetProperty("jumlahPengajuan").GetInt32(), "manager Marketing tidak bisa mengintip pengajuan IT lewat method");
    }

    [Test]
    public async Task ApiMethod_ajukan_massal_lewat_workflow_dengan_hasil_per_dokumen()
    {
        var andi = U("andi");
        var draft = (await Docs.ListAsync(andi, PR, new ListRequest(Filters: [new FilterClause("workflow_state", "=", "Draft")]))).Items.First();
        var result = Json(JsonSerializer.Serialize(await sp.GetRequiredService<ApiMethodRegistry>().InvokeAsync(andi, "ajukan-massal",
            Json($$"""{"nomor":["{{draft["name"]}}","PR-2026-00004","PR-9999-99999"]}"""), sp)));
        var hasil = result.GetProperty("hasil").EnumerateArray().ToList();
        Assert.True(result.GetProperty("berhasil").GetInt32() == 1 && hasil.Count == 3, "1 berhasil dari 3: " + result);
        Assert.True(hasil[0].GetProperty("berhasil").GetBoolean() && hasil[0].GetProperty("status").GetString() == "Menunggu Atasan", "draft milik sendiri diajukan lewat workflow");
        Assert.True(!hasil[1].GetProperty("berhasil").GetBoolean() && !hasil[2].GetProperty("berhasil").GetBoolean(), "milik orang lain & tidak ada → gagal dengan pesan, bukan seluruh panggilan gagal");
        var audit = await Docs.GetAuditAsync(andi, PR, (string)draft["name"]!);
        Assert.True(audit.Any(a => a.ToState == "Menunggu Atasan"), "transisi tercatat di audit seperti dari layar");
    }
}
