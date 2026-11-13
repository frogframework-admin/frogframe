using System.Globalization;
using Framework.Accounting;
using Framework.Documents;
using Framework.Hooks;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Hooks;

// ---------------------------------------------------------------------------------------------
// Marker DocType: nama kelas = nama DocType (DocHookRegistration memakai typeof(TDoc).Name).
// ---------------------------------------------------------------------------------------------
public sealed class Account { }
public sealed class AccountingPeriod { }
public sealed class FiscalYear { }
public sealed class JournalEntry { }
public sealed class SalesInvoice { }
public sealed class SalesCreditNote { }
public sealed class PurchaseInvoice { }
public sealed class PurchaseDebitNote { }
public sealed class PaymentEntry { }
public sealed class CurrencyExchangeRate { }
public sealed class Address { }
public sealed class Contact { }
public sealed class PeriodClosingVoucher { }
public sealed class TaxInvoice { }
public sealed class WithholdingCertificate { }
public sealed class Asset { }
public sealed class DepreciationRun { }
public sealed class AssetDisposal { }
public sealed class ExpenseClaim { }
public sealed class EmployeeAdvance { }
public sealed class BankStatement { }
public sealed class BankReconciliation { }
public sealed class PaymentReconciliation { }
public sealed class Employee { }
public sealed class Position { }
public sealed class OrganizationUnit { }
public sealed class JobChange { }
public sealed class EmployeeSeparation { }
public sealed class EmploymentContract { }
public sealed class LeaveAllocation { }
public sealed class LeaveApplication { }
public sealed class Lead { }
public sealed class Opportunity { }
public sealed class Quotation { }

/// <summary>Akses aman ke Document.Data (nilai sudah dinormalisasi framework: decimal, bool, "yyyy-MM-dd", List&lt;Dictionary&gt;).</summary>
internal static class D
{
    public static string? Str(this IDictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is string { Length: > 0 } s ? s : null;

    public static decimal Dec(this IDictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) ? ToDec(v) : 0m;

    public static bool Bool(this IDictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) && v is true;

    public static DateOnly? Date(this IDictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) ? ToDate(v) : null;

    public static List<Dictionary<string, object?>> Rows(this IDictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is IEnumerable<Dictionary<string, object?>> rows ? rows.ToList() : [];

    public static decimal ToDec(object? v) => v switch
    {
        null => 0m,
        decimal x => x,
        long x => x,
        int x => x,
        double x => (decimal)x,
        string s when decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var r) => r,
        _ => 0m,
    };

    public static DateOnly? ToDate(object? v) => v switch
    {
        DateOnly x => x,
        string s when DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var r) => r,
        _ => null,
    };

    /// <summary>Isi nilai bila field belum ada/kosong. (Jangan memakai d[key] ??= v: getter Dictionary melempar bila key belum ada.)</summary>
    public static void SetIfEmpty(this IDictionary<string, object?> d, string key, object? value)
    {
        if (value is null) return;
        if (!d.TryGetValue(key, out var current) || current is null || current is string { Length: 0 }) d[key] = value;
    }

    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Sama dengan fungsi round() di ekspresi metadata (AwayFromZero).</summary>
    public static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Pengumpul error per field; dilempar sekaligus agar pengguna melihat semua masalah.</summary>
internal sealed class Errors
{
    private readonly List<FieldError> _list = [];
    public void Add(string path, string message) => _list.Add(new FieldError(path, message));
    public bool Any => _list.Count > 0;
    public void ThrowIfAny() { if (_list.Count > 0) throw new ValidationFailedException(_list); }
    public static Exception One(string path, string message) => new ValidationFailedException([new FieldError(path, message)]);
}

/// <summary>
/// Pembaca master langsung dari store (dibatasi tenant, tidak dibatasi role user). Hook harus bisa membaca Account/Company/TaxCode
/// walaupun user yang menyimpan dokumen (mis. Sales, Employee) tidak punya izin baca master tersebut. Di-cache per operasi.
/// </summary>
internal sealed class Masters(IDocumentStore store, string tenant)
{
    private readonly Dictionary<(string, string), Document?> _cache = new();
    public string Tenant => tenant;

    public async Task<Document?> GetAsync(string docType, string? name, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_cache.TryGetValue((docType, name), out var hit)) return hit;
        var doc = await store.GetAsync(tenant, docType, name, ct);
        _cache[(docType, name)] = doc;
        return doc;
    }

    public async Task<Document> RequireAsync(string docType, string? name, string path, string label, CancellationToken ct) =>
        await GetAsync(docType, name, ct) ?? throw Errors.One(path, $"{label} '{name}' not found");

    public async Task<string?> StrAsync(string docType, string? name, string field, CancellationToken ct) =>
        (await GetAsync(docType, name, ct))?.Data.Str(field);

    /// <summary>Single DocType: nama dokumen = nama DocType. Belum diisi = kosong.</summary>
    public async Task<IDictionary<string, object?>> SingleAsync(string docType, CancellationToken ct) =>
        (await GetAsync(docType, docType, ct))?.Data ?? new Dictionary<string, object?>();

    public async Task<IReadOnlyList<Document>> QueryAsync(string docType, IReadOnlyList<FilterClause> filters, CancellationToken ct,
        string? sortField = null, string direction = "asc", int take = 500)
    {
        var sort = new List<SortDef>();
        if (sortField is not null) sort.Add(new SortDef { Field = sortField, Direction = direction });
        return (await store.QueryAsync(new StoreQuery(tenant, docType, RowScope.All, filters, null, [], sort, 0, take), ct)).Items;
    }
}

/// <summary>Perusahaan: mata uang fungsional dan akun default.</summary>
internal sealed record CompanyInfo(string Name, string BaseCurrency, IDictionary<string, object?> Data)
{
    public static async Task<CompanyInfo> LoadAsync(Masters m, string? company, CancellationToken ct)
    {
        var doc = await m.RequireAsync("Company", company, "company", "Company", ct);
        return new(doc.Name, doc.Data.Str("base_currency") ?? "IDR", doc.Data);
    }

    public string? Default(string field) => Data.Str(field);
}

internal static class Rates
{
    /// <summary>Kurs from→to terakhir pada/sebelum tanggal, untuk jenis kurs tertentu; mencoba pasangan terbalik bila perlu.</summary>
    public static async Task<decimal> GetAsync(Masters m, string from, string to, DateOnly date, string rateType, CancellationToken ct)
    {
        if (from == to) return 1m;
        var day = D.Iso(date);
        async Task<decimal?> Find(string a, string b)
        {
            var rows = await m.QueryAsync("CurrencyExchangeRate",
                [new FilterClause("from_currency", "=", a), new FilterClause("to_currency", "=", b),
                 new FilterClause("rate_type", "=", rateType), new FilterClause("rate_date", "<=", day)], ct, "rate_date", "desc", 1);
            var r = rows.FirstOrDefault()?.Data.Dec("exchange_rate");
            return r is > 0m ? r : null;
        }
        if (await Find(from, to) is { } direct) return direct;
        if (await Find(to, from) is { } inverse) return Math.Round(1m / inverse, 6, MidpointRounding.AwayFromZero);
        throw Errors.One("exchange_rate", $"No {rateType} exchange rate {from}→{to} for {day}. Add a Currency Exchange Rate or type the rate manually.");
    }

    /// <summary>Isi exchange_rate dokumen: 1 bila mata uang = mata uang fungsional; dari master kurs bila masih 1/kosong.</summary>
    public static async Task ApplyAsync(Masters m, IDictionary<string, object?> d, CompanyInfo company, DateOnly date, CancellationToken ct)
    {
        var currency = d.Str("currency") ?? company.BaseCurrency;
        if (currency == company.BaseCurrency) { d["exchange_rate"] = 1m; return; }
        var current = d.Dec("exchange_rate");
        if (current > 0 && current != 1m) return; // kurs diketik manual dihormati
        var type = (await m.SingleAsync("AccountingSettings", ct)).Str("default_exchange_rate_type") ?? "BI Middle Rate";
        d["exchange_rate"] = await GetAsync(m, currency, company.BaseCurrency, date, type, ct);
    }
}

internal static class Periods
{
    public const string LockReceivable = "lock_receivable", LockPayable = "lock_payable", LockFixedAsset = "lock_fixed_asset",
        LockPayroll = "lock_payroll";

    /// <summary>
    /// Periode Terbuka yang memuat tanggal, plus kunci subledger dan tanggal beku pembukuan.
    /// Framework juga memeriksa periode saat posting; pemeriksaan ini memberi pesan per field lebih awal.
    /// </summary>
    public static async Task CheckAsync(Masters m, UserContext user, DateOnly date, string? lockField, string path, CancellationToken ct)
    {
        var periods = await m.QueryAsync(Ledger.PeriodDocType, [], ct);
        var period = periods.FirstOrDefault(p => p.Data.Date("start_date") <= date && date <= p.Data.Date("end_date"));
        if (period is null) throw Errors.One(path, $"No accounting period exists for {D.Iso(date)}");
        if (period.Data.Str("status") != "Open") throw Errors.One(path, $"Period {period.Name} is closed");
        if (lockField is not null && period.Data.Bool(lockField) && !user.IsAdmin)
            throw Errors.One(path, $"Period {period.Name} is locked for this transaction (subledger close)");

        var settings = await m.SingleAsync("AccountingSettings", ct);
        if (settings.Date("books_frozen_until") is { } frozen && date <= frozen && !user.IsAdmin)
            throw Errors.One(path, $"Books are frozen until {D.Iso(frozen)}");
    }

    public static async Task CheckFutureAsync(Masters m, DateOnly date, DateOnly today, string path, CancellationToken ct)
    {
        if (date > today && !(await m.SingleAsync("AccountingSettings", ct)).Bool("allow_future_posting"))
            throw Errors.One(path, "The posting date cannot be in the future (see Accounting Settings)");
    }
}

/// <summary>
/// Penyusun baris buku besar dalam mata uang fungsional. Menggabungkan baris berkunci sama, membuang nol,
/// dan menyeimbangkan selisih pembulatan/kurs ke akun yang ditentukan.
/// Dimensi: cost center dikirim sebagai LedgerLine.Department sampai LedgerLine punya dimensi generik.
/// </summary>
internal sealed class Posting(DateOnly date, string? remarks)
{
    private readonly List<LedgerLine> _lines = [];

    public void Dr(string account, decimal amount, string? partyType = null, string? party = null,
        string? againstType = null, string? againstName = null, string? costCenter = null, string? lineRemarks = null)
    {
        amount = D.R2(amount);
        if (amount == 0) return;
        if (amount < 0) { Cr(account, -amount, partyType, party, againstType, againstName, costCenter, lineRemarks); return; }
        _lines.Add(new LedgerLine(date, account, amount, 0m, partyType, party, againstType, againstName, costCenter, lineRemarks ?? remarks));
    }

    public void Cr(string account, decimal amount, string? partyType = null, string? party = null,
        string? againstType = null, string? againstName = null, string? costCenter = null, string? lineRemarks = null)
    {
        amount = D.R2(amount);
        if (amount == 0) return;
        if (amount < 0) { Dr(account, -amount, partyType, party, againstType, againstName, costCenter, lineRemarks); return; }
        _lines.Add(new LedgerLine(date, account, 0m, amount, partyType, party, againstType, againstName, costCenter, lineRemarks ?? remarks));
    }

    public decimal Difference => _lines.Sum(l => l.Debit) - _lines.Sum(l => l.Credit);

    /// <summary>Selisih (debit − kredit) dibukukan ke akun penyeimbang. Selisih di atas toleransi berarti bug perhitungan.</summary>
    public void Balance(string? account, string? costCenter, decimal tolerance, string what)
    {
        var diff = Difference;
        if (diff == 0) return;
        if (Math.Abs(diff) > tolerance)
            throw Errors.One("name", $"Entry is out of balance by {diff:N2} (exceeds the {what} tolerance of {tolerance:N2})");
        if (account is null)
            throw Errors.One("company", $"There is a {what} difference of {diff:N2}; set the {what} account on the Company");
        if (diff > 0) Cr(account, diff, costCenter: costCenter, lineRemarks: $"{what} difference");
        else Dr(account, -diff, costCenter: costCenter, lineRemarks: $"{what} difference");
    }

    /// <summary>Baris akhir: digabung per kunci lalu dinetokan ke satu sisi.</summary>
    public IReadOnlyList<LedgerLine> Build()
    {
        if (_lines.Count < 2) throw Errors.One("name", "A posting needs at least 2 lines");
        return _lines
            .GroupBy(l => (l.Account, l.PartyType, l.Party, l.AgainstVoucherType, l.AgainstVoucherName, l.Department, l.Remarks))
            .Select(g =>
            {
                var net = g.Sum(l => l.Debit) - g.Sum(l => l.Credit);
                var k = g.Key;
                return new LedgerLine(date, k.Account, net > 0 ? net : 0m, net < 0 ? -net : 0m,
                    k.PartyType, k.Party, k.AgainstVoucherType, k.AgainstVoucherName, k.Department, k.Remarks);
            })
            .Where(l => l.Debit != 0 || l.Credit != 0)
            .ToList();
    }
}

internal static class Reversal
{
    /// <summary>
    /// Baris pembalik untuk voucher yang dibatalkan: tanggal asli bila periodenya masih Terbuka, selain itu tanggal pembatalan.
    /// Sama dengan perilaku Apps.Sample (spesifikasi 4.7).
    /// </summary>
    public static async Task ApplyAsync(HookContext c, ILedgerReader ledger, Masters m, TimeProvider clock, CancellationToken ct)
    {
        var original = await ledger.GetVoucherAsync(c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct);
        if (original.Count == 0) return;
        var origDate = original[0].PostingDate;
        var periods = await m.QueryAsync(Ledger.PeriodDocType, [], ct);
        var stillOpen = periods.Any(p => p.Data.Str("status") == "Open"
            && p.Data.Date("start_date") <= origDate && origDate <= p.Data.Date("end_date"));
        c.AddLedgers(Ledger.Reversal(original, stillOpen ? origDate : DateOnly.FromDateTime(clock.GetLocalNow().Date)));
    }
}

/// <summary>Open item dari buku besar: baris pihak yang menunjuk voucher sebagai "against".</summary>
internal static class OpenItems
{
    public sealed record Item(string Account, decimal NetBase, IReadOnlyList<LedgerEntry> Rows);

    /// <summary>Saldo terbuka (debit − kredit, mata uang fungsional) voucher untuk satu pihak, per akun kontrol milik voucher itu.</summary>
    public static async Task<Item?> GetAsync(ILedgerReader ledger, string tenant, string voucherType, string voucherName,
        string? partyType, string? party, CancellationToken ct)
    {
        var rows = (await ledger.GetAgainstAsync(tenant, voucherType, voucherName, ct))
            .Where(e => partyType is null || (e.PartyType == partyType && e.Party == party)).ToList();
        var own = rows.FirstOrDefault(e => e.VoucherType == voucherType && e.VoucherName == voucherName);
        if (own is null) return null;
        var onAccount = rows.Where(e => e.Account == own.Account).ToList();
        return new(own.Account, onAccount.Sum(e => e.Debit - e.Credit), onAccount);
    }

    /// <summary>
    /// Open item voucher untuk satu pihak pada akun yang saldonya searah (positif = sisi debit/piutang, negatif = sisi kredit/utang).
    /// Dipakai untuk voucher yang punya dua open item, mis. Uang Muka Karyawan (piutang uang muka + utang pencairan).
    /// </summary>
    public static async Task<Item?> GetForDirectionAsync(ILedgerReader ledger, string tenant, string voucherType, string voucherName,
        string partyType, string party, bool wantDebitBalance, CancellationToken ct)
    {
        var rows = (await ledger.GetAgainstAsync(tenant, voucherType, voucherName, ct))
            .Where(e => e.PartyType == partyType && e.Party == party).ToList();
        foreach (var account in rows.Where(e => e.VoucherType == voucherType && e.VoucherName == voucherName).Select(e => e.Account).Distinct())
        {
            var onAccount = rows.Where(e => e.Account == account).ToList();
            var ownNet = onAccount.Where(e => e.VoucherType == voucherType && e.VoucherName == voucherName).Sum(e => e.Debit - e.Credit);
            if (ownNet > 0 == wantDebitBalance && ownNet != 0) return new(account, onAccount.Sum(e => e.Debit - e.Credit), onAccount);
        }
        return null;
    }

    /// <summary>Sudah ada pelunasan/nota dari voucher lain yang belum dibatalkan.</summary>
    public static async Task<bool> HasSettlementAsync(ILedgerReader ledger, string tenant, string voucherType, string voucherName, CancellationToken ct) =>
        (await ledger.GetAgainstAsync(tenant, voucherType, voucherName, ct))
            .Where(e => !(e.VoucherType == voucherType && e.VoucherName == voucherName))
            .GroupBy(e => (e.VoucherType, e.VoucherName))
            .Any(g => g.Sum(e => e.Debit - e.Credit) != 0);
}

/// <summary>Event outbox ERP (diproses ErpEventHandler setelah commit; harus idempoten).</summary>
internal static class ErpEvents
{
    public const string Prefix = "erp.";
    public const string PositionSync = "erp.position.sync", OrgManagerChanged = "erp.org.manager_changed",
        JobChangeApply = "erp.jobchange.apply", SeparationApply = "erp.separation.apply", FiscalYearStatus = "erp.fiscalyear.status",
        TaxInvoiceCreate = "erp.taxinvoice.create", PurchaseFollowUps = "erp.purchase.followups",
        QuotationWon = "erp.quotation.won", QuotationBilled = "erp.quotation.billed", AdvanceTouch = "erp.touch",
        EmployeeUser = "erp.employee.user", PaymentRunExecute = "erp.paymentrun.execute";

    public static void Emit(HookContext c, string type, Dictionary<string, string?> payload)
    {
        payload["docType"] = c.Doc.DocType; payload["name"] = c.Doc.Name;
        c.Events.Add(new OutboxMessage
        {
            TenantId = c.Doc.TenantId, Type = type, Payload = payload, CreatedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = $"{c.Doc.TenantId}:{c.Doc.DocType}:{c.Doc.Name}:v{c.Doc.Version}:{type}:{string.Join('|', payload.Values)}",
        });
    }
}

/// <summary>
/// Operasi dokumen sebagai user sistem untuk handler event, job, dan aksi otomatis. Selalu lewat DocumentService
/// (validasi, hook, audit, outbox tetap berjalan). Field readOnly tidak bisa dikirim lewat update; dokumen tujuan
/// menghitungnya sendiri di hook-nya, jadi cukup "disentuh" (update tanpa perubahan).
/// </summary>
internal sealed class SystemDocs(DocumentService docs, string tenant)
{
    public UserContext User { get; } = UserContext.System(tenant);
    public DocumentService Service => docs;

    public async Task<Document?> TryGetAsync(string docType, string name, CancellationToken ct)
    {
        try { return await docs.GetAsync(User, docType, name, ct); }
        catch (DocumentNotFoundException) { return null; }
    }

    public async Task<Document?> UpdateAsync(string docType, string name, IDictionary<string, object?> patch, CancellationToken ct)
    {
        var current = await TryGetAsync(docType, name, ct);
        if (current is null || current.DocStatus != DocStatus.Draft) return null; // hanya draft/master yang bisa diubah
        return await docs.UpdateAsync(User, docType, name, current.Version, patch, ct);
    }

    public Task<Document?> TouchAsync(string docType, string name, CancellationToken ct) =>
        UpdateAsync(docType, name, new Dictionary<string, object?>(), ct);

    public Task<Document> InsertAsync(string docType, IDictionary<string, object?> payload, string audit, CancellationToken ct) =>
        docs.InsertAsync(User, docType, payload, audit, ct);

    public async Task<Document?> ActionAsync(string docType, string name, string action, string? comment, CancellationToken ct)
    {
        var current = await TryGetAsync(docType, name, ct);
        return current is null ? null : await docs.RunActionAsync(User, docType, name, action, current.Version, comment, ct);
    }
}

internal static class Notify
{
    /// <summary>Notifikasi inbox ke user yang terhubung ke karyawan (atribut employee). Id deterministik = idempoten.</summary>
    public static async Task ToEmployeesAsync(IUserDirectory users, INotificationStore inbox, string tenant, IEnumerable<string?> employees,
        string subject, string body, string? docType, string? docName, string key, CancellationToken ct)
    {
        var targets = employees.OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (targets.Count == 0) return;
        foreach (var u in (await users.ListAsync(tenant, ct)).Where(u => u.Attr("employee") is { } e && targets.Contains(e)))
            inbox.Add(new Notification(DeterministicId($"{tenant}|{key}|{u.Id}"), tenant, u.Id, subject, body, docType, docName, DateTimeOffset.UtcNow));
    }

    public static async Task ToRoleAsync(IUserDirectory users, INotificationStore inbox, string tenant, string role,
        string subject, string body, string? docType, string? docName, string key, CancellationToken ct)
    {
        foreach (var u in (await users.ListAsync(tenant, ct)).Where(u => u.Roles.Contains(role)))
            inbox.Add(new Notification(DeterministicId($"{tenant}|{key}|{u.Id}"), tenant, u.Id, subject, body, docType, docName, DateTimeOffset.UtcNow));
    }

    private static Guid DeterministicId(string key) => new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
}

internal static class Accounts
{
    /// <summary>Akun buku besar valid untuk posting: ada, bukan grup, aktif; akun beku hanya untuk System Manager.</summary>
    public static async Task<Document> RequirePostableAsync(Masters m, UserContext user, string? account, string path, CancellationToken ct)
    {
        var acc = await m.RequireAsync("Account", account, path, "Account", ct);
        if (acc.Data.Bool("is_group")) throw Errors.One(path, $"Account {acc.Name} is a group account");
        if (acc.Data.TryGetValue("is_active", out var active) && active is false) throw Errors.One(path, $"Account {acc.Name} is inactive");
        if (acc.Data.Bool("is_frozen") && !user.IsAdmin) throw Errors.One(path, $"Account {acc.Name} is frozen");
        return acc;
    }
}
