using Apps.Erp.Hooks;
using Framework.Accounting;
using Framework.Documents;
using Framework.Jobs;
using Framework.Persistence;
using Framework.Security;

namespace Apps.Erp.Reports;

// ---------------------------------------------------------------------------------------------
// Laporan ERP (IReportProvider). Halaman Reports framework hanya menampilkan filter Date atau Select (dengan Options),
// jadi filter teks bebas (pihak, karyawan) tidak dipakai: saring hasilnya lewat ekspor CSV.
//
// Laporan buku besar (neraca saldo, laba rugi, neraca) membaca ILedgerReader.GetBalancesAsync (patch 0001) langsung dari
// store, jadi konsisten dengan posting dan jalan juga di mode in-memory. Buku besar tidak ada di skema reporting (World 2
// SQL hanya punya view per DocType), sehingga laporan ini tidak bisa dibuat sebagai SQL template.
// Laporan berbasis dokumen (aging, register pajak, saldo cuti, pipeline) memakai DocumentService.QueryForReportAsync agar
// row scope user berlaku (mis. Sales hanya melihat opportunity miliknya). Batas framework: 10.000 dokumen per query.
// ---------------------------------------------------------------------------------------------

internal static class Rpt
{
    public static DateOnly? DateOf(IReadOnlyDictionary<string, string?> f, string key) =>
        f.GetValueOrDefault(key) is { Length: > 0 } s && DateOnly.TryParse(s[..Math.Min(10, s.Length)], out var d) ? d : null;

    public static string? Opt(IReadOnlyDictionary<string, string?> f, string key) => f.GetValueOrDefault(key) is { Length: > 0 } s ? s : null;

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetLocalNow().Date);

    public static Dictionary<string, object?> Row(params (string Key, object? Value)[] cells) => cells.ToDictionary(c => c.Key, c => c.Value);

    /// <summary>Akun (master) per kode: nama, root type, report type, grup.</summary>
    public static async Task<Dictionary<string, Document>> AccountsAsync(Masters m, CancellationToken ct) =>
        (await m.QueryAsync("Account", [], ct, take: 10_000)).ToDictionary(a => a.Name, StringComparer.Ordinal);

    /// <summary>
    /// Saldo per akun dalam [from, to]. Bila excludeClosing, jurnal Tutup Buku Tahunan dikeluarkan supaya laba rugi periode
    /// yang mencakup akhir tahun buku tidak menjadi nol (PCV menolkan akun laba rugi ke laba ditahan).
    /// </summary>
    public static async Task<Dictionary<string, (decimal Dr, decimal Cr)>> BalancesAsync(ILedgerReader ledger, Masters m, string tenant,
        DateOnly? from, DateOnly to, bool excludeClosing, CancellationToken ct)
    {
        var map = new Dictionary<string, (decimal Dr, decimal Cr)>(StringComparer.Ordinal);
        foreach (var b in await ledger.GetBalancesAsync(tenant, from, to, ct))
        {
            var cur = map.GetValueOrDefault(b.Account);
            map[b.Account] = (cur.Dr + b.Debit, cur.Cr + b.Credit);
        }
        if (!excludeClosing) return map;
        foreach (var pcv in await m.QueryAsync("PeriodClosingVoucher", [], ct, take: 1000))
        {
            if (pcv.DocStatus == DocStatus.Draft) continue;
            foreach (var e in await ledger.GetVoucherAsync(tenant, "PeriodClosingVoucher", pcv.Name, ct))
            {
                if (e.PostingDate > to || (from is { } f && e.PostingDate < f)) continue;
                var cur = map.GetValueOrDefault(e.Account);
                map[e.Account] = (cur.Dr - e.Debit, cur.Cr - e.Credit); // pembalik PCV yang dibatalkan ikut ternetral
            }
        }
        return map;
    }

    public static string AccountLabel(Dictionary<string, Document> accounts, string code) =>
        accounts.TryGetValue(code, out var a) && a.Data.Str("account_name") is { } n ? $"{code} {n}" : code;
}

/// <summary>Neraca saldo: saldo awal, mutasi periode, dan saldo akhir per akun (sisi debit/kredit).</summary>
public sealed class TrialBalanceReport(ILedgerReader ledger, IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-trial-balance";
    public string Title => "Trial Balance";
    public string Description => "Opening balance, period movements, and closing balance per account. Opening includes all postings before the start date.";
    public string DocType => "Account";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("from", "From date", "Date"), new("to", "To date", "Date"),
        new("show", "Accounts", "Select", ["With balance or movement", "All active accounts"]),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var m = new Masters(store, user.TenantId);
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? new DateOnly(to.Year, to.Month, 1);
        var all = Rpt.Opt(f, "show") == "All active accounts";
        var accounts = await Rpt.AccountsAsync(m, ct);
        var opening = await Rpt.BalancesAsync(ledger, m, user.TenantId, null, from.AddDays(-1), false, ct);
        var period = await Rpt.BalancesAsync(ledger, m, user.TenantId, from, to, false, ct);

        var codes = accounts.Values.Where(a => !a.Data.Bool("is_group") && all && a.Data.Bool("is_active")).Select(a => a.Name)
            .Concat(opening.Keys).Concat(period.Keys).Distinct().OrderBy(c => c, StringComparer.Ordinal);
        var rows = new List<Dictionary<string, object?>>();
        decimal tod = 0, toc = 0, tpd = 0, tpc = 0, tcd = 0, tcc = 0;
        foreach (var code in codes)
        {
            var o = opening.GetValueOrDefault(code); var p = period.GetValueOrDefault(code);
            var openNet = o.Dr - o.Cr; var closeNet = openNet + p.Dr - p.Cr;
            if (!all && openNet == 0 && p.Dr == 0 && p.Cr == 0) continue;
            var acc = accounts.GetValueOrDefault(code);
            decimal od = Math.Max(openNet, 0), oc = Math.Max(-openNet, 0), cd = Math.Max(closeNet, 0), cc = Math.Max(-closeNet, 0);
            tod += od; toc += oc; tpd += p.Dr; tpc += p.Cr; tcd += cd; tcc += cc;
            rows.Add(Rpt.Row(("account", code), ("account_name", acc?.Data.Str("account_name")), ("root_type", acc?.Data.Str("root_type")),
                ("opening_debit", od), ("opening_credit", oc), ("debit", p.Dr), ("credit", p.Cr), ("closing_debit", cd), ("closing_credit", cc)));
        }
        rows.Add(Rpt.Row(("account", "Total"), ("account_name", tcd == tcc ? "Balanced" : $"Out of balance {tcd - tcc:N2}"), ("root_type", null),
            ("opening_debit", tod), ("opening_credit", toc), ("debit", tpd), ("credit", tpc), ("closing_debit", tcd), ("closing_credit", tcc)));
        return new ReportResult(
        [
            new("account", "Account"), new("account_name", "Account name"), new("root_type", "Root type"),
            new("opening_debit", "Opening debit", "Currency"), new("opening_credit", "Opening credit", "Currency"),
            new("debit", "Debit", "Currency"), new("credit", "Credit", "Currency"),
            new("closing_debit", "Closing debit", "Currency"), new("closing_credit", "Closing credit", "Currency"),
        ], rows);
    }
}

/// <summary>Laba rugi periode: pendapatan − beban, per akun atau per cost center; jurnal tutup buku dikeluarkan.</summary>
public sealed class ProfitAndLossReport(ILedgerReader ledger, IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-profit-and-loss";
    public string Title => "Profit and Loss";
    public string Description => "Income minus expense for the period, per account or per cost center. Year-end closing entries are excluded.";
    public string DocType => "Account";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("from", "From date", "Date"), new("to", "To date", "Date"),
        new("group_by", "Group by", "Select", ["Account", "Cost center"]),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var m = new Masters(store, user.TenantId);
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? new DateOnly(to.Year, 1, 1);
        var accounts = await Rpt.AccountsAsync(m, ct);
        bool IsPl(string code) => accounts.TryGetValue(code, out var a) && a.Data.Str("report_type") == "Profit and Loss";
        string Root(string code) => accounts.TryGetValue(code, out var a) ? a.Data.Str("root_type") ?? "" : "";
        var rows = new List<Dictionary<string, object?>>();

        if (Rpt.Opt(f, "group_by") == "Cost center")
        {
            // Per cost center (LedgerLine.Department), jurnal tutup buku dikeluarkan per (akun, cost center).
            var net = new Dictionary<(string Account, string Cc), decimal>();
            foreach (var x in await ledger.GetBalancesAsync(user.TenantId, from, to, ct))
                net[(x.Account, x.Department ?? "(none)")] = net.GetValueOrDefault((x.Account, x.Department ?? "(none)")) + x.Debit - x.Credit;
            foreach (var pcv in await m.QueryAsync("PeriodClosingVoucher", [], ct, take: 1000))
                if (pcv.DocStatus != DocStatus.Draft)
                    foreach (var e in await ledger.GetVoucherAsync(user.TenantId, "PeriodClosingVoucher", pcv.Name, ct))
                        if (e.PostingDate >= from && e.PostingDate <= to)
                            net[(e.Account, e.Department ?? "(none)")] = net.GetValueOrDefault((e.Account, e.Department ?? "(none)")) - (e.Debit - e.Credit);
            decimal ti = 0, te = 0;
            foreach (var g in net.Where(kv => IsPl(kv.Key.Account)).GroupBy(kv => kv.Key.Cc).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var income = -g.Where(kv => Root(kv.Key.Account) == "Income").Sum(kv => kv.Value);
                var expense = g.Where(kv => Root(kv.Key.Account) == "Expense").Sum(kv => kv.Value);
                if (income == 0 && expense == 0) continue;
                ti += income; te += expense;
                rows.Add(Rpt.Row(("label", g.Key), ("income", income), ("expense", expense), ("net", income - expense)));
            }
            rows.Add(Rpt.Row(("label", "Net profit (loss)"), ("income", ti), ("expense", te), ("net", ti - te)));
            return new ReportResult([new("label", "Cost center"), new("income", "Income", "Currency"), new("expense", "Expense", "Currency"), new("net", "Net", "Currency")], rows);
        }

        var bal = await Rpt.BalancesAsync(ledger, m, user.TenantId, from, to, true, ct);
        decimal totalIncome = 0, totalExpense = 0;
        foreach (var root in new[] { "Income", "Expense" })
        {
            rows.Add(Rpt.Row(("label", root.ToUpperInvariant()), ("amount", null)));
            decimal sub = 0;
            foreach (var code in bal.Keys.Where(c => IsPl(c) && Root(c) == root).OrderBy(c => c, StringComparer.Ordinal))
            {
                var (dr, cr) = bal[code];
                var amount = root == "Income" ? cr - dr : dr - cr;
                if (amount == 0) continue;
                sub += amount;
                rows.Add(Rpt.Row(("label", $"    {Rpt.AccountLabel(accounts, code)}"), ("amount", amount)));
            }
            rows.Add(Rpt.Row(("label", $"Total {root.ToLowerInvariant()}"), ("amount", sub)));
            if (root == "Income") totalIncome = sub; else totalExpense = sub;
        }
        rows.Add(Rpt.Row(("label", "Net profit (loss)"), ("amount", totalIncome - totalExpense)));
        return new ReportResult([new("label", "Account"), new("amount", "Amount", "Currency")], rows);
    }
}

/// <summary>Neraca per tanggal: aset = kewajiban + ekuitas + laba (rugi) yang belum ditutup.</summary>
public sealed class BalanceSheetReport(ILedgerReader ledger, IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-balance-sheet";
    public string Title => "Balance Sheet";
    public string Description => "Assets, liabilities, and equity as of a date. Profit not yet closed to retained earnings is shown as current year earnings.";
    public string DocType => "Account";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("as_of", "As of date", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var m = new Masters(store, user.TenantId);
        var asOf = Rpt.DateOf(f, "as_of") ?? Rpt.Today(clock);
        var accounts = await Rpt.AccountsAsync(m, ct);
        var bal = await Rpt.BalancesAsync(ledger, m, user.TenantId, null, asOf, false, ct);
        string Root(string code) => accounts.TryGetValue(code, out var a) ? a.Data.Str("root_type") ?? "" : "";
        var rows = new List<Dictionary<string, object?>>();
        var totals = new Dictionary<string, decimal>();
        foreach (var root in new[] { "Asset", "Liability", "Equity" })
        {
            rows.Add(Rpt.Row(("label", root.ToUpperInvariant()), ("amount", null)));
            decimal sub = 0;
            foreach (var code in bal.Keys.Where(c => Root(c) == root).OrderBy(c => c, StringComparer.Ordinal))
            {
                var (dr, cr) = bal[code];
                var amount = root == "Asset" ? dr - cr : cr - dr;
                if (amount == 0) continue;
                sub += amount;
                rows.Add(Rpt.Row(("label", $"    {Rpt.AccountLabel(accounts, code)}"), ("amount", amount)));
            }
            if (root == "Equity")
            {
                // Laba (rugi) akun laba rugi yang belum ditutup lewat Tutup Buku Tahunan.
                var unclosed = bal.Where(kv => Root(kv.Key) is "Income" or "Expense").Sum(kv => kv.Value.Cr - kv.Value.Dr);
                if (unclosed != 0) { sub += unclosed; rows.Add(Rpt.Row(("label", "    Current year earnings (not yet closed)"), ("amount", unclosed))); }
            }
            totals[root] = sub;
            rows.Add(Rpt.Row(("label", $"Total {root.ToLowerInvariant()}"), ("amount", sub)));
        }
        var diff = totals["Asset"] - totals["Liability"] - totals["Equity"];
        rows.Add(Rpt.Row(("label", "Total liabilities and equity"), ("amount", totals["Liability"] + totals["Equity"])));
        rows.Add(Rpt.Row(("label", diff == 0 ? "Check: balanced" : "Check: OUT OF BALANCE (accounts without a root type?)"), ("amount", diff)));
        return new ReportResult([new("label", "Account"), new("amount", "Amount", "Currency")], rows);
    }
}

/// <summary>Aging piutang/utang per tanggal dari buku besar: sisa tiap faktur (setelah pembayaran, nota, alokasi) per umur jatuh tempo, plus uang muka.</summary>
public abstract class AgingReport(ILedgerReader ledger, TimeProvider clock) : IReportProvider
{
    protected abstract string InvoiceDocType { get; }
    protected abstract string PartyType { get; }
    protected abstract string PartyField { get; }
    protected abstract bool DebitBalance { get; } // piutang bersaldo debit, utang bersaldo kredit
    public abstract string Name { get; }
    public abstract string Title { get; }
    public string Description => "Open amount per invoice as of a date (after payments, notes, and allocations), aged by due date; unallocated advances shown separately. Functional currency.";
    public string DocType => InvoiceDocType;
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("as_of", "As of date", "Date"),
        new("view", "View", "Select", ["Per invoice", "Per party"]),
    ];

    private static readonly string[] Buckets = ["current", "d1_30", "d31_60", "d61_90", "d90_plus"];

    private static string Bucket(int daysOverdue) => daysOverdue switch
    {
        <= 0 => "current", <= 30 => "d1_30", <= 60 => "d31_60", <= 90 => "d61_90", _ => "d90_plus",
    };

    private async Task<(string? Account, decimal Open)> OpenAsOfAsync(string tenant, string type, string name, string party, DateOnly asOf, CancellationToken ct)
    {
        var rows = (await ledger.GetAgainstAsync(tenant, type, name, ct))
            .Where(e => e.PartyType == PartyType && e.Party == party && e.PostingDate <= asOf).ToList();
        var own = rows.FirstOrDefault(e => e.VoucherType == type && e.VoucherName == name);
        if (own is null) return (null, 0m);
        var net = rows.Where(e => e.Account == own.Account).Sum(e => e.Debit - e.Credit);
        return (own.Account, DebitBalance ? net : -net);
    }

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var asOf = Rpt.DateOf(f, "as_of") ?? Rpt.Today(clock);
        var perParty = Rpt.Opt(f, "view") == "Per party";
        var lines = new List<Dictionary<string, object?>>();

        foreach (var inv in (await documents.QueryForReportAsync(user, InvoiceDocType, [], ct: ct))
                 .Where(d => d.DocStatus != DocStatus.Draft && d.Data.Date("posting_date") <= asOf))
        {
            var party = inv.Data.Str(PartyField);
            if (party is null) continue;
            var (_, open) = await OpenAsOfAsync(user.TenantId, InvoiceDocType, inv.Name, party, asOf, ct);
            if (open == 0) continue;
            var due = inv.Data.Date("due_date") ?? inv.Data.Date("posting_date")!.Value;
            var days = asOf.DayNumber - due.DayNumber;
            var row = Rpt.Row(("party", party), ("party_name", inv.Data.Str($"{PartyField}_name")), ("voucher", inv.Name),
                ("posting_date", inv.Data.Str("posting_date")), ("due_date", D.Iso(due)), ("days_overdue", (decimal)Math.Max(days, 0)), ("open", open));
            foreach (var b in Buckets) row[b] = 0m;
            row[Bucket(days)] = open;
            row["advance"] = 0m;
            lines.Add(row);
        }

        // Uang muka (pembayaran yang belum dialokasikan). Butuh izin laporan PaymentEntry; tanpa itu dilewati.
        try
        {
            foreach (var pay in (await documents.QueryForReportAsync(user, "PaymentEntry", [], ct: ct))
                     .Where(d => d.DocStatus != DocStatus.Draft && d.Data.Str("party_type") == PartyType && d.Data.Date("posting_date") <= asOf))
            {
                var party = pay.Data.Str("party")!;
                var (_, open) = await OpenAsOfAsync(user.TenantId, "PaymentEntry", pay.Name, party, asOf, ct);
                if (open >= 0) continue; // uang muka bersaldo berlawanan dengan tagihan
                var row = Rpt.Row(("party", party), ("party_name", null), ("voucher", pay.Name), ("posting_date", pay.Data.Str("posting_date")),
                    ("due_date", null), ("days_overdue", 0m), ("open", open));
                foreach (var b in Buckets) row[b] = 0m;
                row["advance"] = open;
                lines.Add(row);
            }
        }
        catch (PermissionDeniedException) { }

        List<Dictionary<string, object?>> rows;
        if (perParty)
        {
            rows = lines.GroupBy(l => (string)l["party"]!).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                var r = Rpt.Row(("party", g.Key), ("party_name", g.Select(x => x["party_name"]).OfType<string>().FirstOrDefault()), ("voucher", $"{g.Count()} documents"),
                    ("posting_date", null), ("due_date", null), ("days_overdue", g.Max(x => (decimal)x["days_overdue"]!)), ("open", g.Sum(x => (decimal)x["open"]!)));
                foreach (var b in Buckets.Append("advance")) r[b] = g.Sum(x => (decimal)x[b]!);
                return r;
            }).ToList();
        }
        else rows = lines.OrderBy(l => (string)l["party"]!, StringComparer.Ordinal).ThenBy(l => l["due_date"] as string).ToList();

        var total = Rpt.Row(("party", "Total"), ("party_name", null), ("voucher", null), ("posting_date", null), ("due_date", null), ("days_overdue", null),
            ("open", lines.Sum(x => (decimal)x["open"]!)));
        foreach (var b in Buckets.Append("advance")) total[b] = lines.Sum(x => (decimal)x[b]!);
        rows.Add(total);

        return new ReportResult(
        [
            new("party", PartyType), new("party_name", "Name"), new("voucher", "Document"), new("posting_date", "Posting date"),
            new("due_date", "Due date"), new("days_overdue", "Days overdue", "Int"), new("open", "Open amount", "Currency"),
            new("current", "Not yet due", "Currency"), new("d1_30", "1-30", "Currency"), new("d31_60", "31-60", "Currency"),
            new("d61_90", "61-90", "Currency"), new("d90_plus", "> 90", "Currency"), new("advance", "Advance", "Currency"),
        ], rows);
    }
}

public sealed class ReceivableAgingReport(ILedgerReader ledger, TimeProvider clock) : AgingReport(ledger, clock)
{
    public override string Name => "erp-ar-aging";
    public override string Title => "Accounts Receivable Aging";
    protected override string InvoiceDocType => "SalesInvoice";
    protected override string PartyType => "Customer";
    protected override string PartyField => "customer";
    protected override bool DebitBalance => true;
}

public sealed class PayableAgingReport(ILedgerReader ledger, TimeProvider clock) : AgingReport(ledger, clock)
{
    public override string Name => "erp-ap-aging";
    public override string Title => "Accounts Payable Aging";
    protected override string InvoiceDocType => "PurchaseInvoice";
    protected override string PartyType => "Vendor";
    protected override string PartyField => "vendor";
    protected override bool DebitBalance => false;
}

/// <summary>Register PPN per masa: faktur pajak keluaran & masukan (retur bernilai negatif), dan PPN kurang/lebih bayar.</summary>
public sealed class VatRegisterReport(TimeProvider clock) : IReportProvider
{
    public string Name => "erp-vat-register";
    public string Title => "VAT Register";
    public string Description => "Output and input tax invoices per tax period (returns negative), with VAT payable (output − input). Cancelled and replaced tax invoices are excluded.";
    public string DocType => "TaxInvoice";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("from", "From (tax period)", "Date"), new("to", "To (tax period)", "Date"),
        new("direction", "Direction", "Select", ["Output", "Input"]),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? new DateOnly(to.Year, to.Month, 1);
        string fromP = $"{from:yyyy-MM}", toP = $"{to:yyyy-MM}";
        var direction = Rpt.Opt(f, "direction");
        var docs = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(d => d.Data.Str("status") is not ("Cancelled" or "Replaced"))
            .Where(d => d.Data.Str("tax_period") is { } p && string.CompareOrdinal(p, fromP) >= 0 && string.CompareOrdinal(p, toP) <= 0)
            .Where(d => direction is null || d.Data.Str("direction") == direction)
            .OrderBy(d => d.Data.Str("direction")).ThenBy(d => d.Data.Str("tax_period")).ThenBy(d => d.Data.Str("tax_invoice_date")).ThenBy(d => d.Name);

        var rows = new List<Dictionary<string, object?>>();
        var sums = new Dictionary<string, (decimal Dpp, decimal Vat)>();
        foreach (var d in docs)
        {
            var sign = d.Data.Str("reference_doctype") is "SalesCreditNote" or "PurchaseDebitNote" ? -1m : 1m;
            var dir = d.Data.Str("direction") ?? "";
            var dpp = sign * d.Data.Dec("tax_base"); var vat = sign * d.Data.Dec("vat_amount");
            var s = sums.GetValueOrDefault(dir); sums[dir] = (s.Dpp + dpp, s.Vat + vat);
            rows.Add(Rpt.Row(("direction", dir), ("tax_period", d.Data.Str("tax_period")), ("tax_invoice_no", d.Data.Str("tax_invoice_no")),
                ("tax_invoice_date", d.Data.Str("tax_invoice_date")), ("party", d.Data.Str("party")), ("reference", $"{d.Data.Str("reference_doctype")} {d.Data.Str("reference_name")}"),
                ("status", d.Data.Str("status")), ("tax_base", dpp), ("other_tax_base", sign * d.Data.Dec("other_tax_base")), ("vat_amount", vat)));
        }
        foreach (var (dir, s) in sums.OrderBy(x => x.Key))
            rows.Add(Rpt.Row(("direction", $"Total {dir.ToLowerInvariant()}"), ("tax_base", s.Dpp), ("vat_amount", s.Vat)));
        if (direction is null)
        {
            var net = sums.GetValueOrDefault("Output").Vat - sums.GetValueOrDefault("Input").Vat;
            rows.Add(Rpt.Row(("direction", net >= 0 ? "VAT payable (output − input)" : "VAT overpaid (output − input)"), ("vat_amount", net)));
        }
        return new ReportResult(
        [
            new("direction", "Direction"), new("tax_period", "Tax period"), new("tax_invoice_no", "Tax invoice no."), new("tax_invoice_date", "Date"),
            new("party", "Party"), new("reference", "Source document"), new("status", "Coretax status"),
            new("tax_base", "DPP", "Currency"), new("other_tax_base", "Other tax base", "Currency"), new("vat_amount", "VAT", "Currency"),
        ], rows);
    }
}

/// <summary>Register PPh potong/pungut per masa dan kode pajak.</summary>
public sealed class WithholdingRegisterReport(TimeProvider clock) : IReportProvider
{
    public string Name => "erp-withholding-register";
    public string Title => "Withholding Tax Register";
    public string Description => "Withholding tax certificates per tax period: those we withhold (payable) and those withheld by customers (tax credit), totalled per tax code.";
    public string DocType => "WithholdingCertificate";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("from", "From (tax period)", "Date"), new("to", "To (tax period)", "Date"),
        new("direction", "Direction", "Select", ["We withhold", "Withheld by counterparty"]),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? new DateOnly(to.Year, to.Month, 1);
        string fromP = $"{from:yyyy-MM}", toP = $"{to:yyyy-MM}";
        var direction = Rpt.Opt(f, "direction");
        var docs = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(d => d.DocStatus != DocStatus.Cancelled)
            .Where(d => d.Data.Str("tax_period") is { } p && string.CompareOrdinal(p, fromP) >= 0 && string.CompareOrdinal(p, toP) <= 0)
            .Where(d => direction is null || d.Data.Str("direction") == direction)
            .OrderBy(d => d.Data.Str("direction")).ThenBy(d => d.Data.Str("tax_code")).ThenBy(d => d.Data.Str("certificate_date")).ToList();
        var rows = docs.Select(d => Rpt.Row(("direction", d.Data.Str("direction")), ("tax_code", d.Data.Str("tax_code")), ("tax_period", d.Data.Str("tax_period")),
            ("certificate_no", d.Data.Str("certificate_no")), ("certificate_date", d.Data.Str("certificate_date")), ("party", d.Data.Str("party")),
            ("reference", d.Data.Str("reference_name")), ("gross_amount", d.Data.Dec("gross_amount")), ("tax_amount", d.Data.Dec("tax_amount")))).ToList();
        foreach (var g in docs.GroupBy(d => (Dir: d.Data.Str("direction"), Code: d.Data.Str("tax_code"))).OrderBy(g => g.Key.Dir).ThenBy(g => g.Key.Code))
            rows.Add(Rpt.Row(("direction", $"Total {g.Key.Dir}"), ("tax_code", g.Key.Code), ("gross_amount", g.Sum(d => d.Data.Dec("gross_amount"))),
                ("tax_amount", g.Sum(d => d.Data.Dec("tax_amount")))));
        return new ReportResult(
        [
            new("direction", "Direction"), new("tax_code", "Tax code"), new("tax_period", "Tax period"), new("certificate_no", "Certificate no."),
            new("certificate_date", "Date"), new("party", "Party"), new("reference", "Source document"),
            new("gross_amount", "Tax base", "Currency"), new("tax_amount", "Tax", "Currency"),
        ], rows);
    }
}

/// <summary>Saldo cuti per karyawan per jenis cuti untuk alokasi yang berlaku pada tanggal tertentu.</summary>
public sealed class LeaveBalanceReport(TimeProvider clock) : IReportProvider
{
    public string Name => "erp-leave-balance";
    public string Title => "Leave Balance";
    public string Description => "Per employee and leave type: entitlement of the allocation valid on the date, leave taken (approved), pending approval, and remaining balance.";
    public string DocType => "LeaveAllocation";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("as_of", "As of date", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var asOf = Rpt.DateOf(f, "as_of") ?? Rpt.Today(clock);
        var allocations = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(a => a.DocStatus != DocStatus.Cancelled && a.Data.Date("from_date") <= asOf && a.Data.Date("to_date") >= asOf).ToList();
        IReadOnlyList<Document> applications;
        try { applications = await documents.QueryForReportAsync(user, "LeaveApplication", [], ct: ct); }
        catch (PermissionDeniedException) { applications = []; }

        var rows = new List<Dictionary<string, object?>>();
        foreach (var a in allocations.OrderBy(a => a.Data.Str("employee"), StringComparer.Ordinal).ThenBy(a => a.Data.Str("leave_type")))
        {
            var emp = a.Data.Str("employee"); var type = a.Data.Str("leave_type");
            DateOnly start = a.Data.Date("from_date")!.Value, end = a.Data.Date("to_date")!.Value;
            var mine = applications.Where(x => x.Data.Str("employee") == emp && x.Data.Str("leave_type") == type
                                               && x.Data.Date("from_date") >= start && x.Data.Date("from_date") <= end && x.DocStatus != DocStatus.Cancelled).ToList();
            var taken = (decimal)mine.Where(x => x.WorkflowState == Approval.Approved).Sum(x => x.Data.Dec("total_days"));
            var pending = (decimal)mine.Where(x => x.WorkflowState == "Pending Approval").Sum(x => x.Data.Dec("total_days"));
            var total = a.Data.Dec("total_leaves");
            rows.Add(Rpt.Row(("employee", emp), ("employee_name", a.Data.Str("employee_name")), ("leave_type", type),
                ("period", $"{D.Iso(start)} – {D.Iso(end)}"), ("total_leaves", total), ("taken", taken), ("pending", pending),
                ("balance", total - taken), ("available", total - taken - pending)));
        }
        return new ReportResult(
        [
            new("employee", "Employee"), new("employee_name", "Name"), new("leave_type", "Leave type"), new("period", "Allocation period"),
            new("total_leaves", "Entitlement (days)", "Float"), new("taken", "Taken", "Float"), new("pending", "Pending approval", "Float"),
            new("balance", "Balance", "Float"), new("available", "Available (after pending)", "Float"),
        ], rows);
    }
}

/// <summary>Pipeline penjualan: jumlah, nilai, dan nilai tertimbang opportunity menurut tahap, kategori forecast, sales, atau bulan closing.</summary>
public sealed class SalesPipelineReport : IReportProvider
{
    public string Name => "erp-sales-pipeline";
    public string Title => "Sales Pipeline";
    public string Description => "Opportunity count, amount, and weighted amount (amount × probability) by stage, forecast category, sales rep, or expected close month. Row scope applies: sales reps see their own opportunities.";
    public string DocType => "Opportunity";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
    [
        new("from", "Expected close from", "Date"), new("to", "Expected close to", "Date"),
        new("group_by", "Group by", "Select", ["Stage", "Forecast category", "Sales rep", "Close month"]),
        new("status", "Opportunities", "Select", ["Open only", "Won only", "Lost only"]),
    ];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var filters = new List<FilterClause>();
        if (Rpt.DateOf(f, "from") is { } from) filters.Add(new("expected_close_date", ">=", D.Iso(from)));
        if (Rpt.DateOf(f, "to") is { } to) filters.Add(new("expected_close_date", "<=", D.Iso(to)));
        var status = Rpt.Opt(f, "status");
        var opps = (await documents.QueryForReportAsync(user, DocType, filters, ct: ct)).Where(o => status switch
        {
            "Open only" => o.Data.Str("stage_type") is not ("Won" or "Lost"),
            "Won only" => o.Data.Str("stage_type") == "Won",
            "Lost only" => o.Data.Str("stage_type") == "Lost",
            _ => true,
        }).ToList();

        var groupBy = Rpt.Opt(f, "group_by") ?? "Stage";
        string Key(Document o) => groupBy switch
        {
            "Forecast category" => o.Data.Str("forecast_category") ?? "(none)",
            "Sales rep" => o.Data.Str("sales_rep") ?? "(unassigned)",
            "Close month" => o.Data.Str("expected_close_date") is { Length: >= 7 } d ? d[..7] : "(no date)",
            _ => o.Data.Str("sales_stage") ?? "(none)",
        };
        var rows = opps.GroupBy(Key).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => Rpt.Row(
            ("group", g.Key), ("count", (decimal)g.Count()), ("amount", g.Sum(o => o.Data.Dec("amount"))),
            ("weighted", g.Sum(o => o.Data.Dec("weighted_amount"))),
            ("avg_probability", g.Any() ? Math.Round((decimal)g.Average(o => o.Data.Dec("probability")), 1) : 0m))).ToList();
        rows.Add(Rpt.Row(("group", "Total"), ("count", (decimal)opps.Count), ("amount", opps.Sum(o => o.Data.Dec("amount"))),
            ("weighted", opps.Sum(o => o.Data.Dec("weighted_amount"))), ("avg_probability", null)));
        return new ReportResult(
        [
            new("group", groupBy), new("count", "Opportunities", "Int"), new("amount", "Amount", "Currency"),
            new("weighted", "Weighted amount", "Currency"), new("avg_probability", "Avg. probability (%)", "Float"),
        ], rows);
    }
}

/// <summary>Register payroll per masa: dasar SPT Masa PPh 21 (e-Bupot 21) dan iuran BPJS. Hanya payslip yang sudah submit.</summary>
public sealed class PayrollRegisterReport(TimeProvider clock) : IReportProvider
{
    public string Name => "erp-payroll-register";
    public string Title => "Payroll Register";
    public string Description => "Submitted payslips per period: gross income (bruto), PPh 21 (TER or annual), DTP, BPJS employee and employer contributions, and net pay. Basis for the monthly PPh 21 return and BPJS payments.";
    public string DocType => "Payslip";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("from", "From (period)", "Date"), new("to", "To (period)", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? new DateOnly(to.Year, to.Month, 1);
        string fromP = $"{from:yyyy-MM}", toP = $"{to:yyyy-MM}";
        var slips = (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
            .Where(s => s.DocStatus == DocStatus.Submitted && s.Data.Str("period") is { } p && string.CompareOrdinal(p, fromP) >= 0 && string.CompareOrdinal(p, toP) <= 0)
            .OrderBy(s => s.Data.Str("period")).ThenBy(s => s.Data.Str("employee"), StringComparer.Ordinal).ToList();
        string[] money = ["taxable_gross", "pph21", "pph21_dtp", "pph21_withheld", "employee_contributions", "employer_contributions", "gross_earnings", "net_pay"];
        var rows = slips.Select(s =>
        {
            var r = Rpt.Row(("period", s.Data.Str("period")), ("employee", s.Data.Str("employee")), ("employee_name", s.Data.Str("employee_name")),
                ("ptkp_status", s.Data.Str("ptkp_status")), ("method", s.Data.Bool("is_last_period") ? "Annual (Art. 17)" : $"TER {s.Data.Str("ter_category")} {s.Data.Dec("ter_rate"):0.##}%"));
            foreach (var k in money) r[k] = s.Data.Dec(k);
            return r;
        }).ToList();
        var total = Rpt.Row(("period", "Total"), ("employee", $"{slips.Count} payslips"), ("employee_name", null), ("ptkp_status", null), ("method", null));
        foreach (var k in money) total[k] = slips.Sum(s => s.Data.Dec(k));
        rows.Add(total);
        return new ReportResult(
        [
            new("period", "Period"), new("employee", "Employee"), new("employee_name", "Name"), new("ptkp_status", "PTKP"), new("method", "Method"),
            new("gross_earnings", "Gross earnings", "Currency"), new("taxable_gross", "Bruto (PPh 21)", "Currency"), new("pph21", "PPh 21", "Currency"),
            new("pph21_dtp", "DTP", "Currency"), new("pph21_withheld", "PPh 21 withheld", "Currency"),
            new("employee_contributions", "BPJS employee", "Currency"), new("employer_contributions", "BPJS employer", "Currency"), new("net_pay", "Net pay", "Currency"),
        ], rows);
    }
}

/// <summary>Daftar transfer dari Payment Run (dasar upload bulk transfer ke internet banking; format file tiap bank berbeda, jadi ekspor CSV lalu sesuaikan).</summary>
public sealed class BankTransferListReport(IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-bank-transfer-list";
    public string Title => "Bank Transfer List";
    public string Description => "Transfers created by submitted Payment Runs, one row per Payment Entry, with the beneficiary bank account. Export as CSV for bulk transfer upload.";
    public string DocType => "PaymentRun";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("from", "From payment date", "Date"), new("to", "To payment date", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? to;
        var m = new Masters(store, user.TenantId);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var run in (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
                 .Where(r => r.DocStatus == DocStatus.Submitted && r.Data.Date("posting_date") >= from && r.Data.Date("posting_date") <= to)
                 .OrderBy(r => r.Name, StringComparer.Ordinal))
        {
            var payments = (await m.QueryAsync("PaymentEntry", [new FilterClause("payment_run", "=", run.Name)], ct, take: 5000))
                .Where(p => p.DocStatus == DocStatus.Submitted).ToList();
            foreach (var g in run.Data.Rows("items").GroupBy(r => (Type: r.Str("party_type"), Party: r.Str("party"))))
            {
                var first = g.First();
                var pe = payments.FirstOrDefault(p => p.Data.Str("party_type") == g.Key.Type && p.Data.Str("party") == g.Key.Party);
                rows.Add(Rpt.Row(("payment_run", run.Name), ("payment_date", run.Data.Str("posting_date")), ("payment_entry", pe?.Name ?? "(not created)"),
                    ("party", g.Key.Party), ("bank_name", first.Str("bank_name")), ("account_no", first.Str("account_no")),
                    ("account_holder", first.Str("account_holder")), ("amount", g.Sum(r => r.Dec("amount"))),
                    ("description", string.Join(", ", g.Select(r => r.Str("reference_name"))))));
            }
        }
        rows.Add(Rpt.Row(("payment_run", "Total"), ("payment_entry", $"{rows.Count} transfers"), ("amount", rows.Sum(r => (decimal)r["amount"]!))));
        return new ReportResult(
        [
            new("payment_run", "Payment run"), new("payment_date", "Date"), new("payment_entry", "Payment"), new("party", "Party"),
            new("bank_name", "Bank"), new("account_no", "Account no."), new("account_holder", "Account name"), new("amount", "Amount", "Currency"),
            new("description", "Description"),
        ], rows);
    }
}

/// <summary>Anggaran vs realisasi per akun (dan cost center) untuk tahun buku yang memuat tanggal.</summary>
public sealed class BudgetVsActualReport(ILedgerReader ledger, IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-budget-vs-actual";
    public string Title => "Budget vs Actual";
    public string Description => "Per submitted budget line: annual budget, year-to-date budget (equal months), actual from the ledger up to the date, remaining, and % used.";
    public string DocType => "Budget";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("as_of", "As of date", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var asOf = Rpt.DateOf(f, "as_of") ?? Rpt.Today(clock);
        var m = new Masters(store, user.TenantId);
        var fy = (await m.QueryAsync("FiscalYear", [], ct, take: 200)).FirstOrDefault(y => y.Data.Date("start_date") <= asOf && y.Data.Date("end_date") >= asOf);
        var rows = new List<Dictionary<string, object?>>();
        var cols = new List<ReportColumn>
        {
            new("budget", "Budget"), new("cost_center", "Cost center"), new("account", "Account"), new("annual", "Annual budget", "Currency"),
            new("ytd_budget", "Budget to date", "Currency"), new("actual", "Actual to date", "Currency"), new("remaining", "Remaining (annual)", "Currency"),
            new("used", "% of annual used", "Float"),
        };
        if (fy is null) return new ReportResult(cols, rows);
        var start = fy.Data.Date("start_date")!.Value; var end = fy.Data.Date("end_date")!.Value;
        var totalMonths = Math.Max(1, (end.Year - start.Year) * 12 + end.Month - start.Month + 1);
        var elapsed = Math.Min(totalMonths, (asOf.Year - start.Year) * 12 + asOf.Month - start.Month + 1);
        var balances = await ledger.GetBalancesAsync(user.TenantId, start, asOf, ct);
        var accounts = await Rpt.AccountsAsync(m, ct);
        foreach (var b in (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
                 .Where(b => b.DocStatus == DocStatus.Submitted && b.Data.Str("fiscal_year") == fy.Name).OrderBy(b => b.Name, StringComparer.Ordinal))
        {
            var cc = b.Data.Str("cost_center");
            foreach (var r in b.Data.Rows("accounts"))
            {
                var acc = r.Str("account")!;
                var annual = r.Dec("annual_amount");
                var actual = balances.Where(x => x.Account == acc && (cc is null || x.Department == cc)).Sum(x => x.Debit - x.Credit);
                if (accounts.TryGetValue(acc, out var a) && a.Data.Str("root_type") == "Income") actual = -actual;
                rows.Add(Rpt.Row(("budget", b.Name), ("cost_center", cc ?? "(all)"), ("account", Rpt.AccountLabel(accounts, acc)), ("annual", annual),
                    ("ytd_budget", D.R2(annual * elapsed / totalMonths)), ("actual", actual), ("remaining", annual - actual),
                    ("used", annual == 0 ? (object?)null : Math.Round(actual / annual * 100m, 1))));
            }
        }
        var ta = rows.Sum(r => (decimal)r["annual"]!); var tac = rows.Sum(r => (decimal)r["actual"]!);
        rows.Add(Rpt.Row(("budget", "Total"), ("annual", ta), ("ytd_budget", rows.Sum(r => (decimal)r["ytd_budget"]!)), ("actual", tac),
            ("remaining", ta - tac), ("used", ta == 0 ? (object?)null : Math.Round(tac / ta * 100m, 1))));
        return new ReportResult(cols, rows);
    }
}

/// <summary>
/// Ringkasan PPh 21 tahunan per pegawai tetap, mengikuti baris bukti potong 1721-A1 (dasar pengisian e-Bupot 21). Diambil dari payslip
/// yang sudah submit pada tahun itu; baris 9–20 dari payslip masa terakhir (Desember / bulan keluar). Pegawai yang belum punya payslip
/// masa terakhir ditandai "In progress".
/// </summary>
public sealed class Pph21AnnualReport(IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-pph21-annual";
    public string Title => "PPh 21 Annual Summary (1721-A1)";
    public string Description => "Per employee for the year of the chosen date: salary, other allowances and overtime, employer-paid insurance, bonus/THR, gross, position cost, pension contributions, PTKP, PKP, PPh 21 due and withheld — the lines of the 1721-A1 withholding slip.";
    public string DocType => "Payslip";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("year_of", "Any date in the year", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var year = (Rpt.DateOf(f, "year_of") ?? Rpt.Today(clock)).Year.ToString("0000");
        var m = new Masters(store, user.TenantId);
        var comps = (await m.QueryAsync("SalaryComponent", [], ct, take: 2000)).ToDictionary(x => x.Name, StringComparer.Ordinal);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var g in (await documents.QueryForReportAsync(user, DocType, [], ct: ct))
                 .Where(p => p.DocStatus == DocStatus.Submitted && p.Data.Str("period") is { } per && per.StartsWith(year, StringComparison.Ordinal))
                 .GroupBy(p => p.Data.Str("employee") ?? "").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            decimal salary = 0, allowances = 0, bonus = 0, premiums = 0;
            foreach (var p in g)
            {
                premiums += p.Data.Dec("jkk") + p.Data.Dec("jkm") + p.Data.Dec("kes_employer");
                foreach (var e in p.Data.Rows("earnings"))
                {
                    var c = comps.GetValueOrDefault(e.Str("salary_component") ?? "");
                    if (c is null || !c.Data.Bool("is_taxable")) continue;
                    if (!c.Data.Bool("is_regular")) bonus += e.Dec("amount");
                    else if (c.Data.Bool("in_bpjs_base")) salary += e.Dec("amount");
                    else allowances += e.Dec("amount");
                }
            }
            var last = g.FirstOrDefault(p => p.Data.Bool("is_last_period"));
            var periods = g.Select(p => p.Data.Str("period")!).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var gross = salary + allowances + premiums + bonus;
            var row = Rpt.Row(("employee", g.Key), ("employee_name", g.First().Data.Str("employee_name")), ("ptkp_status", (last ?? g.Last()).Data.Str("ptkp_status")),
                ("months", $"{periods.First()[5..]}–{periods.Last()[5..]}"), ("status", last is null ? "In progress" : "Final"),
                ("l1_salary", salary), ("l3_allowances", allowances), ("l5_insurance", premiums), ("l7_bonus", bonus), ("l8_gross", gross),
                ("l20_withheld", g.Sum(p => p.Data.Dec("pph21"))), ("dtp", g.Sum(p => p.Data.Dec("pph21_dtp"))));
            if (last is not null)
            {
                var ld = last.Data;
                row["l9_position_cost"] = ld.Dec("position_cost"); row["l10_pension"] = ld.Dec("pension_contributions");
                row["l14_net"] = ld.Dec("ytd_taxable_gross") - ld.Dec("position_cost") - ld.Dec("pension_contributions");
                row["l15_ptkp"] = ld.Dec("ptkp_amount"); row["l16_pkp"] = ld.Dec("pkp"); row["l17_tax_due"] = ld.Dec("pph21_annual");
            }
            rows.Add(row);
        }
        return new ReportResult(
        [
            new("employee", "Employee"), new("employee_name", "Name"), new("ptkp_status", "PTKP"), new("months", "Months"), new("status", "Status"),
            new("l1_salary", "1 Salary", "Currency"), new("l3_allowances", "3 Other allowances & overtime", "Currency"),
            new("l5_insurance", "5 Insurance paid by employer", "Currency"), new("l7_bonus", "7 Bonus/THR", "Currency"), new("l8_gross", "8 Gross", "Currency"),
            new("l9_position_cost", "9 Position cost", "Currency"), new("l10_pension", "10 Pension/JHT contributions", "Currency"),
            new("l14_net", "14 Net income", "Currency"), new("l15_ptkp", "15 PTKP", "Currency"), new("l16_pkp", "16 PKP", "Currency"),
            new("l17_tax_due", "17 PPh 21 due", "Currency"), new("l20_withheld", "20 PPh 21 withheld", "Currency"), new("dtp", "of which DTP", "Currency"),
        ], rows);
    }
}

/// <summary>Saldo stok per item & gudang per tanggal (qty, rata-rata, nilai), tanda di bawah titik pesan ulang, dan cek ke saldo akun persediaan.</summary>
public sealed class StockBalanceReport(ILedgerReader ledger, IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-stock-balance";
    public string Title => "Stock Balance";
    public string Description => "Quantity, moving-average rate, and value per item and warehouse as of a date, with items below their reorder level, and a check against the inventory accounts in the ledger.";
    public string DocType => "StockEntry";
    public IReadOnlyList<ReportFilterDef> Filters { get; } =
        [new("as_of", "As of date", "Date"), new("show", "Show", "Select", ["Items in stock", "Below reorder level", "All movements (incl. zero)"])];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var asOf = Rpt.DateOf(f, "as_of") ?? Rpt.Today(clock);
        var show = Rpt.Opt(f, "show") ?? "Items in stock";
        var m = new Masters(store, user.TenantId);
        var bins = Stock.Bins(await Stock.AllAsync(m, null, null, ct), asOf);
        var items = (await m.QueryAsync("Item", [new FilterClause("is_stock_item", "=", true)], ct, take: 50_000)).ToDictionary(i => i.Name, StringComparer.Ordinal);
        var totalsPerItem = bins.GroupBy(kv => kv.Key.Item).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value.Qty));
        var rows = new List<Dictionary<string, object?>>();
        foreach (var ((item, wh), b) in bins.OrderBy(kv => kv.Key.Item, StringComparer.Ordinal).ThenBy(kv => kv.Key.Wh, StringComparer.Ordinal))
        {
            var it = items.GetValueOrDefault(item);
            var reorder = it?.Data.Dec("reorder_level") ?? 0m;
            var below = reorder > 0 && totalsPerItem[item] < reorder;
            if (show == "Items in stock" && b.Qty == 0 && b.Value == 0) continue;
            if (show == "Below reorder level" && !below) continue;
            rows.Add(Rpt.Row(("item", item), ("item_name", it?.Data.Str("item_name")), ("warehouse", wh), ("qty", b.Qty), ("rate", Math.Round(b.Rate, 2)),
                ("value", D.R2(b.Value)), ("reorder", below ? $"Below {reorder:0.##} (reorder {it?.Data.Dec("reorder_qty"):0.##})" : null)));
        }
        var stockValue = bins.Values.Sum(b => b.Value);
        rows.Add(Rpt.Row(("item", "Total"), ("value", D.R2(stockValue))));
        // Cek ke buku besar: semua akun bertipe Inventory.
        var invAccounts = (await m.QueryAsync("Account", [new FilterClause("account_type", "=", "Inventory")], ct, take: 1000)).Select(a => a.Name).ToHashSet();
        var gl = (await ledger.GetBalancesAsync(user.TenantId, null, asOf, ct)).Where(x => invAccounts.Contains(x.Account)).Sum(x => x.Debit - x.Credit);
        rows.Add(Rpt.Row(("item", "Inventory accounts (ledger)"), ("value", gl)));
        rows.Add(Rpt.Row(("item", gl == D.R2(stockValue) ? "Check: stock = ledger" : "Check: DIFFERENCE (manual journals to inventory accounts?)"), ("value", gl - D.R2(stockValue))));
        return new ReportResult(
        [
            new("item", "Item"), new("item_name", "Item name"), new("warehouse", "Warehouse"), new("qty", "Quantity", "Float"),
            new("rate", "Avg. rate", "Currency"), new("value", "Value", "Currency"), new("reorder", "Reorder"),
        ], rows);
    }
}

/// <summary>Kartu stok: mutasi per item-gudang dalam rentang tanggal dengan saldo berjalan.</summary>
public sealed class StockLedgerReport(IDocumentStore store, TimeProvider clock) : IReportProvider
{
    public string Name => "erp-stock-ledger";
    public string Title => "Stock Ledger";
    public string Description => "Stock card: every movement per item and warehouse in the period with running quantity and value (opening balance first).";
    public string DocType => "StockEntry";
    public IReadOnlyList<ReportFilterDef> Filters { get; } = [new("from", "From date", "Date"), new("to", "To date", "Date")];

    public async Task<ReportResult> RunAsync(UserContext user, IReadOnlyDictionary<string, string?> f, DocumentService documents, CancellationToken ct)
    {
        var to = Rpt.DateOf(f, "to") ?? Rpt.Today(clock);
        var from = Rpt.DateOf(f, "from") ?? new DateOnly(to.Year, to.Month, 1);
        var moves = await Stock.AllAsync(new Masters(store, user.TenantId), null, null, ct);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var g in moves.Where(x => x.Date <= to).GroupBy(x => (x.Item, x.Warehouse)).OrderBy(g => g.Key.Item, StringComparer.Ordinal).ThenBy(g => g.Key.Warehouse))
        {
            decimal qty = g.Where(x => x.Date < from).Sum(x => x.Qty), value = g.Where(x => x.Date < from).Sum(x => x.Value);
            var inPeriod = g.Where(x => x.Date >= from).ToList();
            if (inPeriod.Count == 0) continue;
            rows.Add(Rpt.Row(("date", D.Iso(from)), ("item", g.Key.Item), ("warehouse", g.Key.Warehouse), ("voucher", "Opening"),
                ("qty_in", null), ("qty_out", null), ("value_change", null), ("balance_qty", qty), ("balance_value", D.R2(value))));
            foreach (var x in inPeriod)
            {
                qty += x.Qty; value += x.Value;
                rows.Add(Rpt.Row(("date", D.Iso(x.Date)), ("item", x.Item), ("warehouse", x.Warehouse), ("voucher", $"{x.DocType} {x.DocName}"),
                    ("qty_in", x.Qty > 0 ? x.Qty : (object?)null), ("qty_out", x.Qty < 0 ? -x.Qty : (object?)null), ("value_change", x.Value),
                    ("balance_qty", qty), ("balance_value", D.R2(value))));
            }
        }
        return new ReportResult(
        [
            new("date", "Date"), new("item", "Item"), new("warehouse", "Warehouse"), new("voucher", "Document"),
            new("qty_in", "In", "Float"), new("qty_out", "Out", "Float"), new("value_change", "Value change", "Currency"),
            new("balance_qty", "Balance qty", "Float"), new("balance_value", "Balance value", "Currency"),
        ], rows);
    }
}
