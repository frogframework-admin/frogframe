using System.Security.Cryptography;
using System.Text;
using Framework.Documents;
using Framework.Expressions;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Framework.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Framework.Tests.TestDomain;

// ---------- Akun demo (ASP.NET Core Identity) ----------

public static class DemoAccounts
{
    public static readonly (string Id, string Name, string Tenant, string Department, string? Employee, string[] Roles)[] All =
    [
        ("andi", "Andi Pratama", "acme", "IT", "EMP-0001", ["Employee", "User"]),
        ("sari", "Sari Wulandari", "acme", "MARKETING", "EMP-0002", ["Employee", "Sales"]),
        ("budi", "Budi Santoso", "acme", "IT", "EMP-0003", ["Employee", "Manager"]),
        ("maya", "Maya Lestari", "acme", "MARKETING", "EMP-0004", ["Employee", "Manager", "Sales"]),
        ("dewi", "Dewi Anggraini", "acme", "FINANCE", "EMP-0005", ["Employee", "Finance"]),
        ("rina", "Rina Kusuma", "acme", "PURCHASING", "EMP-0006", ["Employee", "Purchasing", "Procurement"]),
        ("citra", "Citra Handayani", "acme", "HR", "EMP-0009", ["Employee", "HR"]),
        ("joko", "Joko Prasetyo", "acme", "WAREHOUSE", "EMP-0010", ["Employee", "Stock"]),
        ("admin", "Admin Sistem", "acme", "IT", null, [UserContext.SystemManager, UserContext.Administrator]),
        ("tono", "Tono Wijaya", "globex", "IT", "EMP-0001", ["Employee", "Manager"]),
    ];

    /// <summary>Membuat akun demo lewat UserManager (password di-hash, kebijakan password berlaku). Hanya bila belum ada akun.</summary>
    public static async Task SeedAsync(IServiceProvider sp, string password)
    {
        await using var scope = sp.CreateAsyncScope();
        if (await scope.ServiceProvider.GetRequiredService<IUserAccountRepository>().AnyAsync()) return;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        foreach (var (id, name, tenant, dept, emp, roles) in All)
        {
            var result = await users.CreateAsync(new UserAccount
            {
                Id = id, UserName = id, Email = $"{id}@{tenant}.co.id", FullName = name, TenantId = tenant,
                Department = dept, EmployeeId = emp, Roles = roles.ToList(),
            }, password);
            if (!result.Succeeded) throw new InvalidOperationException($"Gagal membuat akun demo {id}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
    }
}

// ---------- Aturan domain dalam C# (bukan metadata) ----------

public sealed class PurchaseRequestHandler : DocumentHandlerBase
{
    public override string DocType => "PurchaseRequest";

    public override async Task ValidateAsync(DocumentContext ctx, CancellationToken ct)
    {
        var data = ctx.Doc.Data;
        if (data.GetValueOrDefault("requester") is string requester)
        {
            var employee = await ctx.Lookup.GetAsync("Employee", requester, ct);
            if (employee is not null && employee.Data.GetValueOrDefault("active") is false)
                ctx.AddError("requester", $"Karyawan {requester} sudah tidak aktif");

            // Pemohon hanya boleh mengajukan atas nama dirinya, kecuali tim pengadaan.
            var isDelegate = ctx.User.HasRole("Procurement");
            if (!isDelegate && ctx.Operation == DocumentOperation.Insert && ctx.User.Attr("employee") != requester)
                ctx.AddError("requester", "Anda hanya dapat membuat pengajuan atas nama diri sendiri");
        }

        var vendorChanged = ctx.Previous is null || !Equals(ctx.Previous.Data.GetValueOrDefault("vendor"), data.GetValueOrDefault("vendor"));
        if (vendorChanged && data.GetValueOrDefault("vendor") is string vendorName)
        {
            var vendor = await ctx.Lookup.GetAsync("Vendor", vendorName, ct);
            if (vendor?.Data.GetValueOrDefault("blacklisted") is true)
                ctx.AddError("vendor", $"Pemasok {vendorName} sedang diblokir dan tidak dapat dipilih");
        }
    }

    public override Task BeforeSubmitAsync(DocumentContext ctx, CancellationToken ct)
    {
        // Validasi khusus saat masuk ke status submitted: draft boleh belum lengkap, pengajuan tidak.
        if (Values.IsEmpty(ctx.Doc.Data.GetValueOrDefault("vendor")))
            ctx.AddError("vendor", "Pilih pemasok usulan sebelum mengajukan");
        return Task.CompletedTask;
    }
}

public sealed class NpwpValidator : IFieldValidator
{
    public string Name => "npwp";
    public string? Validate(object? value, CompiledField field) =>
        value is string s && (s.Length is 15 or 16) && s.All(char.IsAsciiDigit) ? null : $"{field.Label} harus 15 atau 16 digit angka";
}

// ---------- Event handler: notifikasi dari outbox ----------

public sealed class WorkflowNotificationHandler(IUserDirectory users, INotificationStore inbox, IDocumentStore store, TimeProvider clock) : IEventHandler
{
    public bool CanHandle(string eventType) => eventType == "workflow.transitioned";

    public async Task HandleAsync(OutboxMessage m, CancellationToken ct)
    {
        var p = m.Payload;
        var doc = await store.GetAsync(m.TenantId, p["docType"]!, p["name"]!, ct);
        if (doc is null) return;
        var dept = doc.Data.GetValueOrDefault("department") as string;
        var total = Values.ToDecimal(doc.Data.GetValueOrDefault("total")) ?? 0;
        var totalText = "Rp" + total.ToString("#,0", Fmt.Id);
        var tenantUsers = await users.ListAsync(m.TenantId, ct);

        IEnumerable<(UserContext User, string Subject, string Body)> targets = p["to"] switch
        {
            "Menunggu Atasan" => tenantUsers.Where(u => u.Roles.Contains("Manager") && u.Attr("department") == dept)
                .Select(u => (u, $"{doc.Name} menunggu persetujuan Anda", $"{p["actorName"]} mengajukan pembelian senilai {totalText}.")),
            "Menunggu Finance" => tenantUsers.Where(u => u.Roles.Contains("Finance"))
                .Select(u => (u, $"{doc.Name} perlu review Finance", $"Disetujui atasan, total {totalText} melewati batas Rp10 juta.")),
            "Disetujui" => tenantUsers.Where(u => u.Id == doc.Owner || u.Roles.Contains("Procurement"))
                .Select(u => (u, $"{doc.Name} disetujui", u.Id == doc.Owner ? $"Pengajuan Anda disetujui oleh {p["actorName"]}." : $"Siap diproses pengadaan ({totalText}).")),
            "Ditolak" => tenantUsers.Where(u => u.Id == doc.Owner)
                .Select(u => (u, $"{doc.Name} ditolak", $"{p["actorName"]}: {p["comment"]}")),
            "Dibatalkan" => tenantUsers.Where(u => u.Id == doc.Owner)
                .Select(u => (u, $"{doc.Name} dibatalkan", $"{p["actorName"]}: {p["comment"]}")),
            _ => []
        };

        foreach (var (user, subject, body) in targets)
        {
            // Id deterministik dari idempotency key + penerima → pengiriman ulang tidak menggandakan notifikasi.
            var id = new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"{m.IdempotencyKey}|{user.Id}")));
            inbox.Add(new Notification(id, m.TenantId, user.Id, subject, body, doc.DocType, doc.Name, clock.GetUtcNow()));
        }
    }
}

// ---------- Report provider C# ----------

public sealed class SpendByDepartmentReport : IReportProvider
{
    public string Name => "spend-by-department";
    public string Title => "Belanja per departemen";
    public string Description => "Jumlah dan nilai pengajuan pembelian per departemen, dipisah menurut status.";
    public string DocType => "PurchaseRequest";

    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("from", "Sejak tanggal", "Date"),
        new("to", "Sampai tanggal", "Date"),
        new("priority", "Prioritas", "Select", ["Rendah", "Normal", "Tinggi"]),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var filters = new List<FilterClause>();
        if (!string.IsNullOrEmpty(f.GetValueOrDefault("from"))) filters.Add(new("request_date", ">=", f["from"]));
        if (!string.IsNullOrEmpty(f.GetValueOrDefault("to"))) filters.Add(new("request_date", "<=", f["to"]));
        if (!string.IsNullOrEmpty(f.GetValueOrDefault("priority"))) filters.Add(new("priority", "=", f["priority"]));

        var docs = await documents.QueryForReportAsync(user, DocType, filters, ct: ct);
        decimal Sum(IEnumerable<Document> ds) => ds.Sum(d => Values.ToDecimal(d.Data.GetValueOrDefault("total")) ?? 0);

        var rows = docs.GroupBy(d => d.Data.GetValueOrDefault("department") as string ?? "(tanpa departemen)")
            .OrderBy(g => g.Key)
            .Select(g => new Dictionary<string, object?>
            {
                ["department"] = g.Key,
                ["count"] = (decimal)g.Count(),
                ["draft"] = Sum(g.Where(d => d.DocStatus == DocStatus.Draft)),
                ["pending"] = Sum(g.Where(d => d.WorkflowState is "Menunggu Atasan" or "Menunggu Finance")),
                ["approved"] = Sum(g.Where(d => d.WorkflowState == "Disetujui")),
            }).ToList();

        return new ReportResult(
        [
            new("department", "Departemen"), new("count", "Jumlah dokumen", "Int"),
            new("draft", "Nilai draft", "Currency"), new("pending", "Nilai menunggu", "Currency"), new("approved", "Nilai disetujui", "Currency"),
        ], rows);
    }
}

// ---------- Data contoh — dibuat lewat DocumentService agar seluruh pipeline ikut teruji ----------

public static class Seeder
{
    public static async Task RunAsync(IServiceProvider sp)
    {
        var docs = sp.GetRequiredService<DocumentService>();
        var meta = sp.GetRequiredService<IMetadataProvider>();
        if (!meta.Current.TryGet("Department", out _)) return;
        var directory = sp.GetRequiredService<IUserDirectory>();
        var contexts = new Dictionary<string, UserContext>();
        foreach (var a in DemoAccounts.All)
            if (await directory.FindAsync(a.Id) is { } u) contexts[a.Id] = u;
        if (!contexts.ContainsKey("admin")) return; // akun demo tidak ada (mis. pilot dengan akun sungguhan)
        UserContext As(string id) => contexts[id];
        var admin = As("admin");
        // Database permanen: data contoh hanya dibuat sekali (saat database masih kosong).
        if ((await docs.ListAsync(admin, "Employee", new ListRequest(PageSize: 1))).Total > 0) return;

        // Struktur organisasi: departemen (berinduk) lebih dulu, karena departemen karyawan adalah Link.
        foreach (var (code, name, parent) in new (string, string, string?)[]
        {
            ("DIREKSI", "Direksi", null), ("IT", "Teknologi Informasi", "DIREKSI"), ("MARKETING", "Pemasaran", "DIREKSI"),
            ("FINANCE", "Keuangan", "DIREKSI"), ("OPERASIONAL", "Operasional", "DIREKSI"), ("INFRASTRUKTUR", "Infrastruktur TI", "IT"),
        })
            await docs.InsertAsync(admin, "Department", new Dictionary<string, object?> { ["code"] = code, ["department_name"] = name, ["parent_department"] = parent });

        var employees = new (string Name, string Dept, string? User, string Jabatan)[]
        {
            ("Andi Pratama", "IT", "andi", "Staf Pengembang"), ("Sari Wulandari", "MARKETING", "sari", "Staf Pemasaran"),
            ("Budi Santoso", "IT", "budi", "Manajer TI"), ("Maya Lestari", "MARKETING", "maya", "Manajer Pemasaran"),
            ("Dewi Anggraini", "FINANCE", "dewi", "Manajer Keuangan"), ("Rina Kusuma", "OPERASIONAL", "rina", "Kepala Pengadaan"),
            ("Joko Susilo", "INFRASTRUKTUR", null, "Teknisi Infrastruktur"), ("Hendra Kusuma", "DIREKSI", null, "Direktur Utama"),
        };
        foreach (var (name, dept, user, jabatan) in employees)
            await docs.InsertAsync(admin, "Employee", new Dictionary<string, object?>
            {
                ["employee_name"] = name, ["department"] = dept, ["user_id"] = user, ["jabatan"] = jabatan,
                ["email"] = $"{name.Split(' ')[0].ToLower()}@acme.co.id", ["active"] = user is not null || jabatan == "Direktur Utama",
            });
        // Atasan langsung (EMP-0008 = Direktur Utama) dan kepala departemen
        foreach (var (emp, boss) in new[] { ("EMP-0001", "EMP-0003"), ("EMP-0007", "EMP-0003"), ("EMP-0002", "EMP-0004"),
                                            ("EMP-0003", "EMP-0008"), ("EMP-0004", "EMP-0008"), ("EMP-0005", "EMP-0008"), ("EMP-0006", "EMP-0008") })
        {
            var e = await docs.GetAsync(admin, "Employee", emp);
            await docs.UpdateAsync(admin, "Employee", emp, e.Version, new Dictionary<string, object?> { ["atasan_id"] = boss });
        }
        foreach (var (dept, head) in new[] { ("DIREKSI", "EMP-0008"), ("IT", "EMP-0003"), ("MARKETING", "EMP-0004"), ("FINANCE", "EMP-0005"),
                                             ("OPERASIONAL", "EMP-0006"), ("INFRASTRUKTUR", "EMP-0007") })
        {
            var d = await docs.GetAsync(admin, "Department", dept);
            await docs.UpdateAsync(admin, "Department", dept, d.Version, new Dictionary<string, object?> { ["head"] = head });
        }

        var rina = As("rina");
        foreach (var (name, cat, npwp, blocked) in new[]
        {
            ("PT Sinar Komputindo", "Perangkat keras", "012345678901234", false),
            ("CV Kertas Nusantara", "ATK", "098765432109876", false),
            ("PT Awan Lunak Indonesia", "Perangkat lunak", "1122334455667788", false),
            ("PT Murah Meriah", "Perangkat keras", "555555555555555", true),
        })
            await docs.InsertAsync(rina, "Vendor", new Dictionary<string, object?>
                { ["vendor_name"] = name, ["category"] = cat, ["npwp"] = npwp, ["blacklisted"] = blocked });

        foreach (var (name, cat, uom, price) in new[]
        {
            ("Laptop 14\" Core i7, 32GB", "Perangkat keras", "unit", 21_500_000m),
            ("Monitor 27 inci", "Perangkat keras", "unit", 2_750_000m),
            ("Mouse nirkabel", "Perangkat keras", "pcs", 185_000m),
            ("Keyboard mekanik", "Perangkat keras", "pcs", 950_000m),
            ("Kertas A4 80gsm", "ATK", "box", 245_000m),
            ("Tinta printer", "ATK", "pcs", 310_000m),
            ("Lisensi desain grafis", "Perangkat lunak", "lisensi", 1_450_000m),
            ("Lisensi office suite", "Perangkat lunak", "lisensi", 1_200_000m),
            ("Jasa servis AC", "Jasa", "unit", 350_000m),
            ("Langganan cloud storage", "Perangkat lunak", "bulan", 600_000m),
            ("Headset rapat", "Perangkat keras", "pcs", 725_000m),
            ("Spidol whiteboard", "ATK", "box", 95_000m),
        })
            await docs.InsertAsync(rina, "Product", new Dictionary<string, object?>
                { ["product_name"] = name, ["category"] = cat, ["uom"] = uom, ["price"] = price });

        static List<Dictionary<string, object?>> Items(params (string Name, decimal Qty, string Uom, decimal Rate)[] items) =>
            items.Select(i => new Dictionary<string, object?> { ["item_name"] = i.Name, ["qty"] = i.Qty, ["uom"] = i.Uom, ["rate"] = i.Rate }).ToList();

        var today = DateOnly.FromDateTime(DateTime.Today);
        async Task<Document> Pr(string user, string vendor, string priority, int daysAgo, string? why, List<Dictionary<string, object?>> items) =>
            await docs.InsertAsync(As(user), "PurchaseRequest", new Dictionary<string, object?>
            {
                ["vendor"] = vendor, ["priority"] = priority, ["justification"] = why, ["items"] = items,
                ["request_date"] = today.AddDays(-daysAgo).ToString("yyyy-MM-dd"),
                ["required_by"] = today.AddDays(14 - daysAgo).ToString("yyyy-MM-dd"),
            });
        async Task<Document> Act(Document d, string user, string action, string? comment = null) =>
            await docs.RunActionAsync(As(user), d.DocType, d.Name, action, d.Version, comment);

        // Draft milik Andi
        await Pr("andi", "VND-0001", "Normal", 0, null, Items(("Mouse nirkabel", 5, "pcs", 185_000), ("Keyboard mekanik", 2, "pcs", 950_000)));
        // Menunggu atasan (Budi) — total kecil
        var pr2 = await Pr("andi", "VND-0002", "Rendah", 2, null, Items(("Kertas A4 80gsm", 10, "box", 245_000), ("Tinta printer", 4, "pcs", 310_000)));
        await Act(pr2, "andi", "Ajukan");
        // Menunggu Finance — total besar, sudah disetujui Budi
        var pr3 = await Pr("andi", "VND-0001", "Tinggi", 5, "Penggantian laptop tim pengembang yang sudah berumur 5 tahun.",
            Items(("Laptop 14\" Core i7, 32GB", 6, "unit", 21_500_000)));
        pr3 = await Act(pr3, "andi", "Ajukan");
        await Act(pr3, "budi", "Setujui & teruskan ke Finance");
        // Disetujui — Marketing
        var pr4 = await Pr("sari", "VND-0003", "Normal", 9, null, Items(("Lisensi desain grafis", 3, "lisensi", 1_450_000)));
        pr4 = await Act(pr4, "sari", "Ajukan");
        await Act(pr4, "maya", "Setujui");
        // Ditolak — Marketing
        var pr5 = await Pr("sari", "VND-0002", "Tinggi", 12, "Kebutuhan pameran.", Items(("Spanduk & backdrop pameran", 1, "unit", 7_800_000)));
        pr5 = await Act(pr5, "sari", "Ajukan");
        await Act(pr5, "maya", "Tolak", "Anggaran pameran Q4 sudah dialokasikan ke agensi. Ajukan ulang dengan rincian ukuran.");

        // Tenant lain — tidak boleh terlihat oleh tenant acme
        var tono = As("tono");
        var gAdmin = tono with { Roles = new HashSet<string> { UserContext.SystemManager } };
        await docs.InsertAsync(gAdmin, "Department", new Dictionary<string, object?> { ["code"] = "IT", ["department_name"] = "Teknologi Informasi" });
        await docs.InsertAsync(gAdmin, "Employee", new Dictionary<string, object?> { ["employee_name"] = "Tono Wijaya", ["department"] = "IT", ["user_id"] = "tono", ["active"] = true });
        await docs.InsertAsync(gAdmin, "Vendor", new Dictionary<string, object?> { ["vendor_name"] = "Globex Supplies", ["category"] = "ATK", ["npwp"] = "999999999999999" });
        await docs.InsertAsync(tono, "PurchaseRequest", new Dictionary<string, object?>
        {
            ["vendor"] = "VND-0001", ["items"] = Items(("Rahasia Globex: server rak", 2, "unit", 38_000_000)),
            ["priority"] = "Tinggi", ["justification"] = "Data milik tenant globex.",
        });
    }
}
