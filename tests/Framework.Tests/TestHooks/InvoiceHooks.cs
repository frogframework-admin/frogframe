using Framework.Accounting;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;
using Framework.Security;

namespace Framework.Tests.TestHooks;

public sealed class SalesInvoice { }
public sealed class PurchaseInvoice { }

/// <summary>Satu tarif pajak dari master (disalin ke perhitungan saat faktur disimpan).</summary>
public sealed record TaxInfo(string Code, decimal Rate, decimal Factor, bool FloorRupiah, string OutputAccount, string InputAccount);

/// <summary>Hasil perhitungan satu baris faktur.</summary>
public sealed record InvoiceLine(int Index, decimal Gross, decimal Discount, decimal Net, string? Account, string? Department, string? TaxCode);

/// <summary>
/// Perhitungan faktur (spesifikasi V1.3): nilai baris = round(qty × harga, 2); diskon baris (nominal, atau persen dari bruto);
/// pajak dihitung SEKALI per tarif dari Σ DPP tarif itu, dengan mode pembulatan dari master (default: bulat ke bawah ke Rupiah).
/// Nilai yang dikirim klien untuk field hitungan selalu ditimpa.
/// </summary>
public static class InvoiceMath
{
    public static decimal Tax(decimal dpp, TaxInfo t)
    {
        var raw = dpp * t.Factor * t.Rate / 100m;
        return t.FloorRupiah ? decimal.Floor(raw) : decimal.Round(raw, 2, MidpointRounding.AwayFromZero);
    }

    public static async Task<(List<InvoiceLine> Lines, Dictionary<string, (TaxInfo Tax, decimal Base, decimal Amount)> Taxes)> ComputeAsync(
        HookContext c, string accountField, CancellationToken ct)
    {
        var errors = new List<FieldError>();
        var items = (c.Doc.Data.GetValueOrDefault("items") as IEnumerable<Dictionary<string, object?>> ?? []).ToList();
        var lines = new List<InvoiceLine>();
        var taxInfo = new Dictionary<string, TaxInfo>();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i]; var path = $"items[{i}]";
            var qty = Acc.Dec(it.GetValueOrDefault("qty")); var rate = Acc.Dec(it.GetValueOrDefault("rate"));
            if (qty <= 0) errors.Add(new($"{path}.qty", "Qty harus lebih dari 0 (retur memakai nota kredit, bukan faktur negatif)"));
            if (decimal.Round(qty, 4) != qty) errors.Add(new($"{path}.qty", "Qty maksimal 4 desimal"));
            if (rate < 0) errors.Add(new($"{path}.rate", "Harga tidak boleh negatif"));
            var gross = decimal.Round(qty * rate, 2, MidpointRounding.AwayFromZero);
            var discAmt = Acc.Dec(it.GetValueOrDefault("discount_amount"));
            var discPct = Acc.Dec(it.GetValueOrDefault("discount_percent"));
            var disc = discAmt > 0 ? discAmt : decimal.Round(gross * discPct / 100m, 2, MidpointRounding.AwayFromZero);
            if (disc > gross) errors.Add(new($"{path}.discount_amount", "Diskon melebihi nilai baris"));
            var net = gross - disc;
            it["amount"] = net;
            var taxCode = it.GetValueOrDefault("tax_rate") as string;
            if (taxCode is not null && !taxInfo.ContainsKey(taxCode))
            {
                var t = await c.Docs.GetAsync("TaxRate", taxCode, ct);
                taxInfo[taxCode] = new(taxCode, Acc.Dec(t.Data.GetValueOrDefault("rate")),
                    t.Data.GetValueOrDefault("tax_base_factor") is null ? 1m : Acc.Dec(t.Data.GetValueOrDefault("tax_base_factor")),
                    !Equals(t.Data.GetValueOrDefault("rounding"), "Half-up (2 desimal)"),
                    (string)t.Data["output_account"]!, (string)t.Data["input_account"]!);
            }
            lines.Add(new(i, gross, disc, net, it.GetValueOrDefault(accountField) as string, it.GetValueOrDefault("department") as string, taxCode));
        }
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var taxes = lines.Where(l => l.TaxCode is not null).GroupBy(l => l.TaxCode!)
            .ToDictionary(g => g.Key, g => { var b = g.Sum(l => l.Net); return (Tax: taxInfo[g.Key], Base: b, Amount: Tax(b, taxInfo[g.Key])); });
        var d = c.Doc.Data;
        d["gross_total"] = lines.Sum(l => l.Gross);
        d["discount_total"] = lines.Sum(l => l.Discount);
        d["subtotal"] = lines.Sum(l => l.Net);
        d["tax_amount"] = taxes.Values.Sum(t => t.Amount);
        d["grand_total"] = lines.Sum(l => l.Net) + taxes.Values.Sum(t => t.Amount);
        if ((decimal)d["grand_total"]! <= 0) throw new ValidationFailedException([new FieldError("grand_total", "Total tagihan harus lebih dari 0")]);
        return (lines, taxes);
    }

    public static async Task<Document> SettingsAsync(HookContext c, CancellationToken ct)
    {
        try { return await c.Docs.GetAsync("AccountingSettings", "AccountingSettings", ct); }
        catch (DocumentNotFoundException) { throw new ValidationFailedException([new FieldError("name", "Isi Pengaturan Akuntansi terlebih dahulu (akun default)")]); }
    }

    public static string Require(string? value, string field, string what) =>
        value ?? throw new ValidationFailedException([new FieldError(field, $"{what} belum diisi (di faktur, master mitra, atau Pengaturan Akuntansi)")]);
}

/// <summary>Faktur Penjualan: hitung saat simpan; posting saat submit (diskon GROSS ke akun potongan penjualan, Q4); pembalik saat cancel.</summary>
public sealed class SalesInvoiceHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<SalesInvoice>, IAfterSave<SalesInvoice>, IOnCancel<SalesInvoice>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct) => await InvoiceMath.ComputeAsync(c, "income_account", ct);

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var (lines, taxes) = await InvoiceMath.ComputeAsync(c, "income_account", ct);
        var s = (await InvoiceMath.SettingsAsync(c, ct)).Data;
        var customer = (string)c.Doc.Data["customer"]!;
        var date = Acc.Date(c.Doc.Data.GetValueOrDefault("posting_date"));
        var receivable = InvoiceMath.Require(await c.Docs.GetFieldAsync("Customer", customer, "receivable_account", ct) as string
                                             ?? s.GetValueOrDefault("default_receivable_account") as string, "customer", "Akun piutang");
        var grand = (decimal)c.Doc.Data["grand_total"]!;
        c.AddLedger(new(date, receivable, grand, 0, "Customer", customer, c.Doc.DocType, c.Doc.Name));
        var disc = lines.Sum(l => l.Discount);
        if (disc > 0)
            c.AddLedger(new(date, InvoiceMath.Require(s.GetValueOrDefault("default_sales_discount_account") as string, "items", "Akun potongan penjualan"), disc, 0));
        foreach (var g in lines.Where(l => l.Gross > 0).GroupBy(l => (Acc: l.Account ?? s.GetValueOrDefault("default_income_account") as string, l.Department)))
            c.AddLedger(new(date, InvoiceMath.Require(g.Key.Acc, "items", "Akun pendapatan"), 0, g.Sum(l => l.Gross), Department: g.Key.Department));
        foreach (var t in taxes.Values.Where(t => t.Amount > 0).GroupBy(t => t.Tax.OutputAccount))
            c.AddLedger(new(date, t.Key, 0, t.Sum(x => x.Amount)));
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        // Faktur yang sudah menerima pembayaran tidak bisa dibatalkan; batalkan pembayarannya dulu (V1.2 4.7).
        if (await OpenItems.HasSettlementAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct))
            throw new ValidationFailedException([new FieldError("name", $"{c.Doc.Name} sudah menerima pembayaran. Batalkan pembayarannya terlebih dahulu.")]);
        await Acc.ReverseAsync(c, ledger, store, clock, ct);
    }
}

/// <summary>Faktur Pembelian: diskon mengurangi beban langsung (NET, K2); nomor faktur pemasok tidak boleh ganda per pemasok.</summary>
public sealed class PurchaseInvoiceHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<PurchaseInvoice>, IBeforeInsert<PurchaseInvoice>, IBeforeUpdate<PurchaseInvoice>, IAfterSave<PurchaseInvoice>, IOnCancel<PurchaseInvoice>
{
    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct) => await InvoiceMath.ComputeAsync(c, "expense_account", ct);

    private async Task CheckDuplicateAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.Data.GetValueOrDefault("vendor") is not string v || c.Doc.Data.GetValueOrDefault("supplier_invoice_no") is not string no) return;
        var found = await store.QueryAsync(new StoreQuery(c.Doc.TenantId, c.Doc.DocType, RowScope.All,
            [new FilterClause("vendor", "=", v), new FilterClause("supplier_invoice_no", "=", no)], null, [], [], 0, 5), ct);
        if (found.Items.FirstOrDefault(d => d.Name != c.Doc.Name && d.DocStatus != DocStatus.Cancelled) is { } dup)
            throw new ValidationFailedException([new FieldError("supplier_invoice_no", $"Faktur pemasok {no} sudah dicatat di {dup.Name}")]);
    }
    public Task BeforeInsertAsync(HookContext c, CancellationToken ct) => CheckDuplicateAsync(c, ct);
    public Task BeforeUpdateAsync(HookContext c, IReadOnlyList<FieldChange> changes, CancellationToken ct) => CheckDuplicateAsync(c, ct);

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var (lines, taxes) = await InvoiceMath.ComputeAsync(c, "expense_account", ct);
        var s = (await InvoiceMath.SettingsAsync(c, ct)).Data;
        var vendor = (string)c.Doc.Data["vendor"]!;
        var date = Acc.Date(c.Doc.Data.GetValueOrDefault("posting_date"));
        // Akun utang per pemasok belum ada (Pemasok tetap v1); memakai akun utang default dari Pengaturan Akuntansi.
        var payable = InvoiceMath.Require(s.GetValueOrDefault("default_payable_account") as string, "vendor", "Akun utang");
        foreach (var g in lines.Where(l => l.Net > 0).GroupBy(l => (Acc: l.Account ?? s.GetValueOrDefault("default_expense_account") as string, l.Department)))
            c.AddLedger(new(date, InvoiceMath.Require(g.Key.Acc, "items", "Akun beban"), g.Sum(l => l.Net), 0, Department: g.Key.Department));
        foreach (var t in taxes.Values.Where(t => t.Amount > 0).GroupBy(t => t.Tax.InputAccount))
            c.AddLedger(new(date, t.Key, t.Sum(x => x.Amount), 0));
        c.AddLedger(new(date, payable, 0, (decimal)c.Doc.Data["grand_total"]!, "Vendor", vendor, c.Doc.DocType, c.Doc.Name));
    }

    public async Task OnCancelAsync(HookContext c, CancellationToken ct)
    {
        // Faktur yang sudah menerima pembayaran tidak bisa dibatalkan; batalkan pembayarannya dulu (V1.2 4.7).
        if (await OpenItems.HasSettlementAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct))
            throw new ValidationFailedException([new FieldError("name", $"{c.Doc.Name} sudah menerima pembayaran. Batalkan pembayarannya terlebih dahulu.")]);
        await Acc.ReverseAsync(c, ledger, store, clock, ct);
    }
}
