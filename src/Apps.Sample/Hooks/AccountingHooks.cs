using System.Globalization;
using Framework.Accounting;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Sample.Hooks;

/// <summary>Marker DocType akuntansi.</summary>
public sealed class Account { }
public sealed class AccountingPeriod { }
public sealed class JournalEntry { }
public sealed class AccountingSettings { }

internal static class Acc
{
    public static DateOnly Date(object? v) => v switch
    {
        DateOnly d => d,
        string s => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => throw new ValidationFailedException([new FieldError("posting_date", "Tanggal posting wajib diisi")]),
    };
    public static decimal Dec(object? v) => v switch { decimal d => d, long l => l, int i => i, null => 0m, _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture) };

    /// <summary>Pembalik untuk voucher yang dibatalkan. Tanggal asli bila periodenya masih Terbuka, selain itu tanggal pembatalan (Q13).</summary>
    public static async Task ReverseAsync(HookContext c, ILedgerReader ledger, IDocumentStore store, TimeProvider clock, CancellationToken ct)
    {
        var original = await ledger.GetVoucherAsync(c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct);
        if (original.Count == 0) return;
        var origDate = original[0].PostingDate;
        var stillOpen = (await PeriodsAsync(store, c.Doc.TenantId, ct)).Any(p => Equals(p.Data.GetValueOrDefault("status"), "Terbuka")
            && Date(p.Data.GetValueOrDefault("start_date")) <= origDate && origDate <= Date(p.Data.GetValueOrDefault("end_date")));
        c.Ledger.AddRange(Ledger.Reversal(original, stillOpen ? origDate : DateOnly.FromDateTime(clock.GetLocalNow().Date)));
    }

    public static async Task<IReadOnlyList<Document>> PeriodsAsync(IDocumentStore store, string tenant, CancellationToken ct) =>
        (await store.QueryAsync(new StoreQuery(tenant, Ledger.PeriodDocType, RowScope.All, [], null, [], [], 0, 500), ct)).Items;
}

/// <summary>Akun: tipe akun anak sama dengan induknya; kode dan sifat akun yang sudah dipakai tidak boleh berubah.</summary>
public sealed class AccountRulesHook(ILedgerReader ledger) : IBeforeInsert<Account>, IBeforeUpdate<Account>, IBeforeDelete<Account>
{
    private static async Task CheckParentAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.Data.GetValueOrDefault("parent_account") is not string parent) return;
        var root = await c.Docs.GetFieldAsync("Account", parent, "root_type", ct) as string;
        if (root is not null && !Equals(root, c.Doc.Data.GetValueOrDefault("root_type")))
            throw new ValidationFailedException([new FieldError("root_type", $"Tipe akun harus sama dengan akun induk ({root})")]);
    }

    public Task BeforeInsertAsync(HookContext c, CancellationToken ct) => CheckParentAsync(c, ct);

    public async Task BeforeUpdateAsync(HookContext c, IReadOnlyList<FieldChange> changes, CancellationToken ct)
    {
        await CheckParentAsync(c, ct);
        if (c.Previous is null) return;
        if (!Equals(c.Previous.Data.GetValueOrDefault("code"), c.Doc.Data.GetValueOrDefault("code")))
            throw new ValidationFailedException([new FieldError("code", "Kode akun tidak bisa diubah setelah dibuat")]);
        if (await ledger.AccountUsedAsync(c.Doc.TenantId, c.Doc.Name, ct))
            foreach (var f in new[] { "root_type", "is_group" })
                if (!Equals(c.Previous.Data.GetValueOrDefault(f), c.Doc.Data.GetValueOrDefault(f)))
                    throw new ValidationFailedException([new FieldError(f, "Akun sudah dipakai di buku besar; tipe dan status grup tidak bisa diubah")]);
    }

    public async Task BeforeDeleteAsync(HookContext c, CancellationToken ct)
    {
        if (await ledger.AccountUsedAsync(c.Doc.TenantId, c.Doc.Name, ct))
            throw new ValidationFailedException([new FieldError("name", $"Akun {c.Doc.Name} sudah dipakai di buku besar dan tidak bisa dihapus. Nonaktifkan saja.")]);
    }
}

/// <summary>Periode: mulai ≤ akhir dan tidak tumpang tindih dengan periode lain.</summary>
public sealed class AccountingPeriodRulesHook(IDocumentStore store) : IBeforeInsert<AccountingPeriod>, IBeforeUpdate<AccountingPeriod>
{
    private async Task CheckAsync(HookContext c, CancellationToken ct)
    {
        var start = Acc.Date(c.Doc.Data.GetValueOrDefault("start_date"));
        var end = Acc.Date(c.Doc.Data.GetValueOrDefault("end_date"));
        if (start > end) throw new ValidationFailedException([new FieldError("end_date", "Tanggal akhir harus sama atau setelah tanggal mulai")]);
        foreach (var p in await Acc.PeriodsAsync(store, c.Doc.TenantId, ct))
        {
            if (p.Name == c.Doc.Name) continue;
            var ps = Acc.Date(p.Data.GetValueOrDefault("start_date")); var pe = Acc.Date(p.Data.GetValueOrDefault("end_date"));
            if (start <= pe && ps <= end)
                throw new ValidationFailedException([new FieldError("start_date", $"Periode tumpang tindih dengan {p.Name} ({ps:yyyy-MM-dd} s.d. {pe:yyyy-MM-dd})")]);
        }
    }
    public Task BeforeInsertAsync(HookContext c, CancellationToken ct) => CheckAsync(c, ct);
    public Task BeforeUpdateAsync(HookContext c, IReadOnlyList<FieldChange> changes, CancellationToken ct) => CheckAsync(c, ct);
}

/// <summary>
/// Jurnal Umum: submit → baris jurnal diposting ke buku besar dalam transaksi yang sama; cancel → baris pembalik.
/// Tanggal pembalik = tanggal asli bila periodenya masih Terbuka, selain itu tanggal pembatalan (spesifikasi 4.7).
/// </summary>
public sealed class JournalEntryPostingHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock) : IAfterSave<JournalEntry>, IOnCancel<JournalEntry>
{
    public Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return Task.CompletedTask;
        var date = Acc.Date(c.Doc.Data.GetValueOrDefault("posting_date"));
        var items = c.Doc.Data.GetValueOrDefault("items") as IEnumerable<Dictionary<string, object?>> ?? [];
        var header = c.Doc.Data.GetValueOrDefault("remarks") as string;
        var lines = items.Select(i => new LedgerLine(date, (string)i["account"]!, Acc.Dec(i.GetValueOrDefault("debit")), Acc.Dec(i.GetValueOrDefault("credit")),
            Department: i.GetValueOrDefault("department") as string, Remarks: i.GetValueOrDefault("remarks") as string ?? header)).ToList();
        if (lines.Count < 2) throw new ValidationFailedException([new FieldError("items", "Jurnal minimal 2 baris")]);
        c.Ledger.AddRange(lines);
        return Task.CompletedTask;
    }

    public Task OnCancelAsync(HookContext c, CancellationToken ct) => Acc.ReverseAsync(c, ledger, store, clock, ct);
}

/// <summary>
/// Q15: akun kontrol (piutang/utang usaha) tidak boleh dipakai Jurnal Umum — mutasinya wajib lewat faktur,
/// pembayaran, atau saldo awal agar buku pembantu per mitra selalu sama dengan buku besar.
/// Tenant bisa melonggarkan per akun dengan mematikan flag is_control_account (tercatat di audit).
/// </summary>
public sealed class JournalEntryRulesHook : IBeforeInsert<JournalEntry>, IBeforeUpdate<JournalEntry>
{
    private static async Task CheckAsync(HookContext c, CancellationToken ct)
    {
        var items = c.Doc.Data.GetValueOrDefault("items") as IEnumerable<Dictionary<string, object?>> ?? [];
        var errors = new List<FieldError>();
        var i = 0;
        foreach (var it in items)
        {
            if (it.GetValueOrDefault("account") is string acc && await c.Docs.GetFieldAsync("Account", acc, "is_control_account", ct) is true)
                errors.Add(new($"items[{i}].account", $"Akun {acc} adalah akun kontrol piutang/utang; gunakan faktur, pembayaran, atau saldo awal"));
            i++;
        }
        if (errors.Count > 0) throw new ValidationFailedException(errors);
    }
    public Task BeforeInsertAsync(HookContext c, CancellationToken ct) => CheckAsync(c, ct);
    public Task BeforeUpdateAsync(HookContext c, IReadOnlyList<FieldChange> changes, CancellationToken ct) => CheckAsync(c, ct);
}

/// <summary>Akun piutang/utang default wajib akun kontrol (Q15).</summary>
public sealed class AccountingSettingsRulesHook : IBeforeInsert<AccountingSettings>, IBeforeUpdate<AccountingSettings>
{
    private static async Task CheckAsync(HookContext c, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        foreach (var f in new[] { "default_receivable_account", "default_payable_account" })
            if (c.Doc.Data.GetValueOrDefault(f) is string acc && await c.Docs.GetFieldAsync("Account", acc, "is_control_account", ct) is not true)
                errors.Add(new(f, $"Akun {acc} harus ditandai sebagai akun kontrol"));
        if (errors.Count > 0) throw new ValidationFailedException(errors);
    }
    public Task BeforeInsertAsync(HookContext c, CancellationToken ct) => CheckAsync(c, ct);
    public Task BeforeUpdateAsync(HookContext c, IReadOnlyList<FieldChange> changes, CancellationToken ct) => CheckAsync(c, ct);
}
