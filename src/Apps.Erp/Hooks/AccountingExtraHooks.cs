using Framework.Accounting;
using Framework.Documents;
using Framework.Hooks;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;

namespace Apps.Erp.Hooks;

/// <summary>Kurs unik per (tanggal, jenis, dari, ke).</summary>
public sealed class CurrencyExchangeRateRulesHook(IDocumentStore store) : IBeforeInsert<CurrencyExchangeRate>, IBeforeUpdate<CurrencyExchangeRate>
{
    private async Task CheckAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var dup = (await new Masters(store, c.Doc.TenantId).QueryAsync("CurrencyExchangeRate",
            [new FilterClause("rate_date", "=", d.Str("rate_date")), new FilterClause("rate_type", "=", d.Str("rate_type")),
             new FilterClause("from_currency", "=", d.Str("from_currency")), new FilterClause("to_currency", "=", d.Str("to_currency"))], ct, take: 5))
            .FirstOrDefault(x => x.Name != c.Doc.Name);
        if (dup is not null) throw Errors.One("rate_date", $"The same exchange rate already exists in {dup.Name}");
    }
    public Task BeforeInsertAsync(HookContext c, CancellationToken ct) => CheckAsync(c, ct);
    public Task BeforeUpdateAsync(HookContext c, IReadOnlyList<FieldChange> changes, CancellationToken ct) => CheckAsync(c, ct);
}

/// <summary>Hanya satu Address/Contact "utama" per pihak yang ditautkan (child PartyLink).</summary>
public sealed class PartyPrimaryHook(IDocumentStore store)
    : IBeforeInsert<Address>, IBeforeUpdate<Address>, IBeforeInsert<Contact>, IBeforeUpdate<Contact>
{
    private async Task CheckAsync(HookContext c, CancellationToken ct)
    {
        if (!c.Doc.Data.Bool("is_primary")) return;
        var mine = c.Doc.Data.Rows("links").Select(r => (T: r.Str("link_doctype"), N: r.Str("link_name"))).Where(x => x.N is not null).ToHashSet();
        var others = await new Masters(store, c.Doc.TenantId).QueryAsync(c.Doc.DocType, [new FilterClause("is_primary", "=", true)], ct, take: 500);
        foreach (var o in others.Where(o => o.Name != c.Doc.Name))
            foreach (var r in o.Data.Rows("links"))
                if (mine.Contains((r.Str("link_doctype"), r.Str("link_name"))))
                    throw Errors.One("is_primary", $"{r.Str("link_name")} already has a primary {c.Meta.Def.Title.ToLowerInvariant()}: {o.Name}");
    }
    Task IBeforeInsert<Address>.BeforeInsertAsync(HookContext c, CancellationToken ct) => CheckAsync(c, ct);
    Task IBeforeUpdate<Address>.BeforeUpdateAsync(HookContext c, IReadOnlyList<FieldChange> changes, CancellationToken ct) => CheckAsync(c, ct);
    Task IBeforeInsert<Contact>.BeforeInsertAsync(HookContext c, CancellationToken ct) => CheckAsync(c, ct);
    Task IBeforeUpdate<Contact>.BeforeUpdateAsync(HookContext c, IReadOnlyList<FieldChange> changes, CancellationToken ct) => CheckAsync(c, ct);
}

/// <summary>
/// Tutup buku tahunan: saldo semua akun Laba Rugi tahun buku itu dinolkan ke akun laba ditahan; laba bersih dicatat
/// di dokumen; tahun buku ditandai Ditutup (lewat event). Satu PCV aktif per tahun buku.
/// Butuh ILedgerReader.GetBalancesAsync (patches/0001-ledger-get-balances.patch).
/// </summary>
public sealed class PeriodClosingVoucherHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<PeriodClosingVoucher>, IAfterSave<PeriodClosingVoucher>, IOnCancel<PeriodClosingVoucher>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct)
    {
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var fy = await m.RequireAsync("FiscalYear", d.Str("fiscal_year"), "fiscal_year", "Fiscal year", ct);
        if (fy.Data.Str("status") == "Closed") throw Errors.One("fiscal_year", $"Fiscal year {fy.Name} is already closed");
        d.SetIfEmpty("posting_date", fy.Data.Str("end_date"));
        if (d.Date("posting_date") != fy.Data.Date("end_date"))
            throw Errors.One("posting_date", $"The posting date must be the last day of the fiscal year ({fy.Data.Str("end_date")})");
        var dup = (await m.QueryAsync(c.Doc.DocType, [new FilterClause("fiscal_year", "=", fy.Name)], ct, take: 10))
            .FirstOrDefault(x => x.Name != c.Doc.Name && x.DocStatus == DocStatus.Submitted);
        if (dup is not null) throw Errors.One("fiscal_year", $"Fiscal year {fy.Name} was already closed by {dup.Name}");
        await CompanyInfo.LoadAsync(m, d.Str("company"), ct);
    }

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var m = new Masters(store, c.Doc.TenantId);
        var d = c.Doc.Data;
        var fy = await m.RequireAsync("FiscalYear", d.Str("fiscal_year"), "fiscal_year", "Fiscal year", ct);
        var date = d.Date("posting_date")!.Value;
        await Periods.CheckAsync(m, c.User, date, null, "posting_date", ct);
        var closing = d.Str("closing_account") ?? throw Errors.One("closing_account", "The retained earnings account is required");

        var balances = await ledger.GetBalancesAsync(c.Doc.TenantId, fy.Data.Date("start_date"), date, ct);
        var posting = new Posting(date, $"Year-end close {fy.Name}");
        decimal profit = 0;
        foreach (var b in balances.Where(b => b.Net != 0))
        {
            var acc = await m.GetAsync("Account", b.Account, ct);
            if (acc?.Data.Str("report_type") != "Profit and Loss") continue;
            // Nolkan saldo akun (per cost center) dengan sisi berlawanan.
            if (b.Net > 0) posting.Cr(b.Account, b.Net, costCenter: b.Department); else posting.Dr(b.Account, -b.Net, costCenter: b.Department);
            profit -= b.Net; // pendapatan bersaldo kredit (net negatif) menambah laba
        }
        if (profit > 0) posting.Cr(closing, profit); else posting.Dr(closing, -profit);
        d["net_profit"] = profit;
        if (posting.Difference != 0) throw Errors.One("name", "The closing entry is out of balance");
        try { c.Ledger.AddRange(posting.Build()); }
        catch (ValidationFailedException) { /* tidak ada saldo laba rugi: tetap tutup tahun buku tanpa jurnal */ }
        ErpEvents.Emit(c, ErpEvents.FiscalYearStatus, new() { ["fiscal_year"] = fy.Name, ["status"] = "Closed" });
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        await Reversal.ApplyAsync(c, ledger, new Masters(store, c.Doc.TenantId), clock, ct);
        ErpEvents.Emit(c, ErpEvents.FiscalYearStatus, new() { ["fiscal_year"] = c.Doc.Data.Str("fiscal_year"), ["status"] = "Open" });
    }
}

/// <summary>
/// Harian: jurnal dengan auto_reverse yang sudah lewat reversal_date dibuatkan jurnal pembalik (debit/kredit ditukar) lalu di-submit.
/// Idempoten: jurnal yang sudah punya pembalik (reversal_of) dilewati.
/// </summary>
public sealed class JournalAutoReverseJob : IScheduledJob
{
    public string Name => "Erp.JournalAutoReverse";
    public string Schedule => "@daily";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var store = (IDocumentStore)context.Services.GetService(typeof(IDocumentStore))!;
        var m = new Masters(store, context.TenantId);
        var sys = new SystemDocs(context.Docs, context.TenantId);
        var today = DateOnly.FromDateTime(context.ScheduledTime.Date);
        var candidates = await m.QueryAsync("JournalEntry", [new FilterClause("auto_reverse", "=", true)], ct, take: 500);
        var existing = (await m.QueryAsync("JournalEntry", [new FilterClause("reversal_of", "!=", null)], ct, take: 5000))
            .Where(j => j.DocStatus != DocStatus.Cancelled).Select(j => j.Data.Str("reversal_of")).ToHashSet();

        foreach (var je in candidates.Where(j => j.DocStatus == DocStatus.Submitted && j.Data.Date("reversal_date") <= today && !existing.Contains(j.Name)))
        {
            try
            {
                var d = je.Data;
                var lines = d.Rows("accounts").Select(r => (IDictionary<string, object?>)new Dictionary<string, object?>
                {
                    ["account"] = r.Str("account"), ["debit"] = r.Dec("credit"), ["credit"] = r.Dec("debit"),
                    ["party_type"] = r.Str("party_type"), ["party"] = r.Str("party"), ["cost_center"] = r.Str("cost_center"),
                    ["project"] = r.Str("project"), ["location"] = r.Str("location"), ["description"] = r.Str("description"),
                }).ToList();
                var draft = await sys.InsertAsync("JournalEntry", new Dictionary<string, object?>
                {
                    ["company"] = d.Str("company"), ["voucher_type"] = d.Str("voucher_type"), ["posting_date"] = d.Str("reversal_date"),
                    ["currency"] = d.Str("currency"), ["exchange_rate"] = d.Dec("exchange_rate"),
                    ["remarks"] = $"Auto reversal of {je.Name}: {d.Str("remarks")}", ["reversal_of"] = je.Name, ["accounts"] = lines,
                }, "Created automatically (reversal)", ct);
                await sys.ActionAsync("JournalEntry", draft.Name, DocActions.Submit, null, ct);
                context.Logger.LogJobCompleted($"{Name}: {je.Name} → {draft.Name}", context.TenantId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Logger.LogJobFailed(ex, $"{Name} ({je.Name})", context.TenantId);
            }
        }
    }
}
