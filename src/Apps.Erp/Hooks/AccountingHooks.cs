using System.Globalization;
using Framework.Accounting;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;

namespace Apps.Erp.Hooks;

/// <summary>
/// Akun: kelompok sama dengan induk; kode tidak bisa diubah; akun yang sudah dipakai buku besar terkunci sifatnya
/// dan tidak bisa dihapus; akun grup yang masih punya anak tidak bisa dijadikan akun posting.
/// </summary>
public sealed class AccountRulesHook(ILedgerReader ledger, IDocumentStore store)
    : IBeforeInsert<Account>, IBeforeUpdate<Account>, IBeforeDelete<Account>
{
    private static readonly string[] LockedWhenUsed = ["root_type", "report_type", "normal_balance", "is_group"];

    private async Task CheckParentAsync(HookContext c, Masters m, CancellationToken ct)
    {
        var parent = c.Doc.Data.Str("parent_account");
        if (parent is null) return;
        var root = await m.StrAsync("Account", parent, "root_type", ct);
        if (root is not null && root != c.Doc.Data.Str("root_type"))
            throw Errors.One("root_type", $"The root type must match the parent account ({root})");
    }

    private async Task<bool> HasChildrenAsync(Masters m, string name, CancellationToken ct) =>
        (await m.QueryAsync("Account", [new FilterClause("parent_account", "=", name)], ct, take: 1)).Count > 0;

    public Task BeforeInsertAsync(HookContext c, CancellationToken ct) => CheckParentAsync(c, new Masters(store, c.Doc.TenantId), ct);

    public async Task BeforeUpdateAsync(HookContext c, IReadOnlyList<FieldChange> changes, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        await CheckParentAsync(c, m, ct);
        if (c.Previous is null) return;
        if (c.Previous.Data.Str("account_code") != c.Doc.Data.Str("account_code"))
            throw Errors.One("account_code", "The account number cannot be changed after creation (number = document name)");
        if (c.Previous.Data.Bool("is_group") && !c.Doc.Data.Bool("is_group") && await HasChildrenAsync(m, c.Doc.Name, ct))
            throw Errors.One("is_group", "This account still has child accounts; move them first");
        if (await ledger.AccountUsedAsync(c.Doc.TenantId, c.Doc.Name, ct))
            foreach (var f in LockedWhenUsed)
                if (!Equals(c.Previous.Data.GetValueOrDefault(f), c.Doc.Data.GetValueOrDefault(f)))
                    throw Errors.One(f, "The account is already used in the ledger; root type, report type, normal balance, and group status cannot be changed");
    }

    public async Task BeforeDeleteAsync(HookContext c, CancellationToken ct)
    {
        if (await ledger.AccountUsedAsync(c.Doc.TenantId, c.Doc.Name, ct))
            throw Errors.One("name", $"Account {c.Doc.Name} is already used in the ledger and cannot be deleted. Deactivate it instead.");
        if (await HasChildrenAsync(new Masters(store, c.Doc.TenantId), c.Doc.Name, ct))
            throw Errors.One("name", $"Account {c.Doc.Name} still has child accounts");
    }
}

/// <summary>Tahun buku tidak tumpang tindih.</summary>
public sealed class FiscalYearRulesHook(IDocumentStore store) : IBeforeInsert<FiscalYear>, IBeforeUpdate<FiscalYear>
{
    private async Task CheckAsync(HookContext c, CancellationToken ct)
    {
        var start = c.Doc.Data.Date("start_date"); var end = c.Doc.Data.Date("end_date");
        if (start is null || end is null) return; // wajib diisi dicek metadata
        foreach (var y in await new Masters(store, c.Doc.TenantId).QueryAsync("FiscalYear", [], ct))
        {
            if (y.Name == c.Doc.Name) continue;
            if (start <= y.Data.Date("end_date") && y.Data.Date("start_date") <= end)
                throw Errors.One("start_date", $"Overlaps with fiscal year {y.Name}");
        }
    }
    public Task BeforeInsertAsync(HookContext c, CancellationToken ct) => CheckAsync(c, ct);
    public Task BeforeUpdateAsync(HookContext c, IReadOnlyList<FieldChange> changes, CancellationToken ct) => CheckAsync(c, ct);
}

/// <summary>
/// Periode: tidak tumpang tindih, berada di dalam tahun bukunya, jejak penutupan dicatat, dan membuka kembali
/// periode yang sudah ditutup hanya oleh System Manager.
/// </summary>
public sealed class AccountingPeriodRulesHook(IDocumentStore store, TimeProvider clock)
    : IBeforeInsert<AccountingPeriod>, IBeforeUpdate<AccountingPeriod>
{
    private async Task CheckAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var start = d.Date("start_date"); var end = d.Date("end_date");
        if (start is null || end is null) return;
        var m = new Masters(store, c.Doc.TenantId);

        if (await m.GetAsync("FiscalYear", d.Str("fiscal_year"), ct) is { } fy
            && (start < fy.Data.Date("start_date") || end > fy.Data.Date("end_date")))
            throw Errors.One("start_date", $"The period must fall within fiscal year {fy.Name}");

        foreach (var p in await m.QueryAsync(Ledger.PeriodDocType, [], ct))
        {
            if (p.Name == c.Doc.Name) continue;
            if (start <= p.Data.Date("end_date") && p.Data.Date("start_date") <= end)
                throw Errors.One("start_date", $"The period overlaps with {p.Name}");
        }

        var was = c.Previous?.Data.Str("status") ?? "Open";
        var now = d.Str("status");
        if (was != "Closed" && now == "Closed")
        {
            d["closed_on"] = clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            d["closed_by"] = c.User.FullName;
        }
        else if (was == "Closed" && now != "Closed")
        {
            if (!c.User.IsAdmin) throw Errors.One("status", "Only a System Manager can reopen a closed period");
            d["closed_on"] = null; d["closed_by"] = null;
        }
    }
    public Task BeforeInsertAsync(HookContext c, CancellationToken ct) => CheckAsync(c, ct);
    public Task BeforeUpdateAsync(HookContext c, IReadOnlyList<FieldChange> changes, CancellationToken ct) => CheckAsync(c, ct);
}

/// <summary>
/// Jurnal Umum. Simpan: isi kurs & nama akun, validasi akun kontrol/pihak/cost center.
/// Submit: posting baris dalam mata uang fungsional (base_debit/base_credit hasil compute metadata).
/// Akun kontrol hanya untuk "Opening Balance" dan menjadi open item (against = jurnal ini) sehingga bisa dilunasi Payment Entry.
/// Cancel: ditolak bila open item-nya sudah dilunasi; selain itu baris pembalik.
/// </summary>
public sealed class JournalEntryHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<JournalEntry>, IAfterSave<JournalEntry>, IOnCancel<JournalEntry>
{
    private const string OpeningType = "Opening Balance";

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().Date);

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var company = await CompanyInfo.LoadAsync(m, d.Str("company"), ct);
        var date = d.Date("posting_date") ?? Today;
        await Rates.ApplyAsync(m, d, company, date, ct);

        var settings = await m.SingleAsync("AccountingSettings", ct);
        var enforcePl = !settings.ContainsKey("enforce_cost_center_on_pl") || settings.Bool("enforce_cost_center_on_pl");
        var isOpening = d.Str("voucher_type") == OpeningType;
        var rows = d.Rows("accounts");
        var errors = new Errors();
        if (rows.Count < 2) errors.Add("accounts", "A journal needs at least 2 lines");

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i]; var p = $"accounts[{i}]";
            Document acc;
            try { acc = await Accounts.RequirePostableAsync(m, c.User, row.Str("account"), $"{p}.account", ct); }
            catch (ValidationFailedException ex) { foreach (var e in ex.Errors) errors.Add(e.Path, e.Message); continue; }
            row["account_name"] = acc.Data.Str("account_name");

            var type = acc.Data.Str("account_type");
            var isControl = acc.Data.Bool("is_control_account");
            if (isControl && !isOpening)
                errors.Add($"{p}.account", $"Account {acc.Name} is a control account; use invoices/payments, or an Opening Entry journal");
            if ((isControl || type is "Receivable" or "Payable") && row.Str("party") is null)
                errors.Add($"{p}.party", $"Account {acc.Name} requires a party");
            if (type == "Receivable" && row.Str("party_type") is "Vendor")
                errors.Add($"{p}.party_type", "A receivable account cannot be used for a supplier");
            if (type == "Payable" && row.Str("party_type") is "Customer")
                errors.Add($"{p}.party_type", "A payable account cannot be used for a customer");

            var needsCc = acc.Data.Bool("requires_cost_center") || (enforcePl && acc.Data.Str("report_type") == "Profit and Loss");
            if (needsCc && row.Str("cost_center") is null)
                errors.Add($"{p}.cost_center", $"Account {acc.Name} requires a cost center");
        }
        errors.ThrowIfAny();
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var date = d.Date("posting_date") ?? throw Errors.One("posting_date", "The posting date is required");
        await Periods.CheckAsync(m, c.User, date, null, "posting_date", ct);
        await Periods.CheckFutureAsync(m, date, Today, "posting_date", ct);
        var company = await CompanyInfo.LoadAsync(m, d.Str("company"), ct);

        var posting = new Posting(date, d.Str("remarks"));
        foreach (var row in d.Rows("accounts"))
        {
            var account = row.Str("account")!;
            var isControl = (await m.GetAsync("Account", account, ct))?.Data.Bool("is_control_account") == true;
            var partyType = row.Str("party_type"); var party = row.Str("party");
            string? againstType = null, againstName = null;
            if (isControl && party is not null) { againstType = c.Doc.DocType; againstName = c.Doc.Name; }
            posting.Dr(account, row.Dec("base_debit"), partyType, party, againstType, againstName, row.Str("cost_center"), row.Str("description"));
            posting.Cr(account, row.Dec("base_credit"), partyType, party, againstType, againstName, row.Str("cost_center"), row.Str("description"));
        }
        posting.Balance(company.Default("round_off_account"), company.Default("round_off_cost_center"), 1m, "round-off");
        var lines = posting.Build();
        await BudgetControl.CheckAsync(c, m, ledger, lines, company.Name, ct);
        c.Ledger.AddRange(lines);
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        if (await OpenItems.HasSettlementAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct))
            throw Errors.One("name", $"Opening balances in {c.Doc.Name} have been settled; cancel the payments first");
        await Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
    }
}
