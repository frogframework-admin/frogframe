using Framework.Api;
using Framework.Documents;
using Framework.Persistence;
using Framework.Security;
using Framework.Metadata;

namespace Apps.Sample.Domain;

/// <summary>
/// Contoh method BACA: sisa anggaran pengadaan suatu departemen. Data diambil lewat DocumentService, jadi hasil
/// mengikuti hak akses pemanggil (manager IT tidak bisa mengintip pengajuan Marketing).
/// </summary>
public sealed class SisaAnggaranMethod(IConfiguration config) : IApiMethod
{
    public string Name => "sisa-anggaran";
    public string Description => "Anggaran, terpakai (pengajuan disetujui / menunggu Finance), dan sisa anggaran pengadaan per departemen.";
    public IReadOnlyList<string> Roles => ["Finance", "Manager"];
    public IReadOnlyList<ApiMethodParam> Parameters =>
    [
        new("departemen", ArgTypes.Text, Required: true, Description: "Kode departemen, mis. IT"),
        new("tahun", ArgTypes.Number, Description: "Tahun pengajuan; kosong = tahun berjalan"),
    ];

    public async Task<object?> InvokeAsync(ApiMethodContext ctx, CancellationToken ct)
    {
        var dept = ctx.Arg<string>("departemen")!;
        var tahun = (int)(ctx.Arg<decimal?>("tahun") ?? DateTime.Today.Year);
        if (tahun is < 2000 or > 2100) throw new ApiMethodException("tahun di luar rentang wajar");
        var docs = ctx.Services.GetRequiredService<DocumentService>();
        var rows = await docs.QueryForReportAsync(ctx.User, "PurchaseRequest",
        [
            new FilterClause("department", "=", dept),
            new FilterClause("workflow_state", "in", new List<object?> { "Disetujui", "Menunggu Finance" }),
            new FilterClause("request_date", ">=", $"{tahun}-01-01"),
            new FilterClause("request_date", "<=", $"{tahun}-12-31"),
        ], DocActions.Report, ct);
        var anggaran = config.GetValue<decimal?>($"Anggaran:{dept}") ?? config.GetValue("Anggaran:Default", 500_000_000m);
        var terpakai = rows.Sum(d => d.Data.GetValueOrDefault("total") as decimal? ?? 0m);
        return new { departemen = dept, tahun, anggaran, terpakai, sisa = anggaran - terpakai, jumlahPengajuan = rows.Count };
    }
}

/// <summary>
/// Contoh method TULIS: mengajukan beberapa pengajuan draft sekaligus lewat aksi workflow "Ajukan". Setiap dokumen
/// diproses lewat jalur workflow biasa (role, kondisi, validasi, audit) — hasil per dokumen dilaporkan.
/// </summary>
public sealed class AjukanMassalMethod : IApiMethod
{
    public string Name => "ajukan-massal";
    public string Description => "Mengajukan beberapa pengajuan pembelian draft milik pemanggil sekaligus (maks. 50).";
    public IReadOnlyList<string> Roles => ["Employee"];
    public IReadOnlyList<ApiMethodParam> Parameters => [new("nomor", ArgTypes.List, Required: true, Description: "Daftar nomor pengajuan, mis. [\"PR-2026-00003\"]")];

    public async Task<object?> InvokeAsync(ApiMethodContext ctx, CancellationToken ct)
    {
        var nomor = ctx.Arg<List<string>>("nomor")!;
        if (nomor.Count is 0 or > 50) throw new ApiMethodException("isi 1 sampai 50 nomor pengajuan");
        var docs = ctx.Services.GetRequiredService<DocumentService>();
        var hasil = new List<object>();
        foreach (var n in nomor.Distinct())
        {
            try
            {
                var doc = await docs.GetAsync(ctx.User, "PurchaseRequest", n, ct);
                var after = await docs.RunActionAsync(ctx.User, "PurchaseRequest", n, "Ajukan", doc.Version, null, ct);
                hasil.Add(new { nomor = n, berhasil = true, status = after.WorkflowState });
            }
            catch (Exception ex) when (ex is DocumentNotFoundException or PermissionDeniedException or InvalidStateException or ValidationFailedException or ConcurrencyException)
            {
                hasil.Add(new { nomor = n, berhasil = false, pesan = ex.Message });
            }
        }
        return new { diproses = hasil.Count, berhasil = hasil.Count(h => (bool)h.GetType().GetProperty("berhasil")!.GetValue(h)!), hasil };
    }
}
