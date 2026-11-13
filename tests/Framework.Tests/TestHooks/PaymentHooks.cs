using Framework.Accounting;
using Framework.Documents;
using Framework.Hooks;
using Framework.Persistence;
using Framework.Security;

namespace Framework.Tests.TestHooks;

public sealed class PaymentEntry { }

/// <summary>Sisa tagihan faktur, dihitung dari buku besar (bukan field yang disimpan).</summary>
public static class OpenItems
{
    public static string VoucherTypeFor(string paymentType) => paymentType == "Pengeluaran" ? "PurchaseInvoice" : "SalesInvoice";

    /// <summary>Penjualan: Σ(debit − kredit) baris piutang faktur; pembelian: Σ(kredit − debit) baris utang faktur.</summary>
    public static async Task<decimal> OutstandingAsync(ILedgerReader ledger, string tenant, string voucherType, string voucherName, CancellationToken ct)
    {
        var rows = await ledger.GetAgainstAsync(tenant, voucherType, voucherName, ct);
        var net = rows.Sum(e => e.Debit - e.Credit);
        return voucherType == "PurchaseInvoice" ? -net : net;
    }

    /// <summary>Apakah faktur sudah menerima pelunasan dari voucher lain (pembayaran yang belum dibatalkan).</summary>
    public static async Task<bool> HasSettlementAsync(ILedgerReader ledger, string tenant, string voucherType, string voucherName, CancellationToken ct) =>
        (await ledger.GetAgainstAsync(tenant, voucherType, voucherName, ct)).Where(e => e.VoucherName != voucherName)
            .GroupBy(e => e.VoucherName).Any(g => g.Sum(e => e.Debit - e.Credit) != 0);
}

/// <summary>
/// Pembayaran (V1.2 4.5 + Q7): alokasi ke faktur yang sudah di-submit milik mitra yang sama, tidak melebihi sisa tagihan,
/// dan jumlah dibayar = total dialokasikan (selisih ditolak). Penjaga akhir sisa tagihan ada di store (dalam transaksi).
/// </summary>
public sealed class PaymentEntryHook(ILedgerReader ledger, IDocumentStore store, TimeProvider clock)
    : IBeforeValidate<PaymentEntry>, IAfterSave<PaymentEntry>, IOnCancel<PaymentEntry>
{
    private sealed record Alloc(int Index, string Invoice, decimal Amount);

    private async Task<(string Type, string Party, string VoucherType, List<Alloc> Allocs)> CheckAsync(HookContext c, CancellationToken ct)
    {
        var d = c.Doc.Data;
        var type = d.GetValueOrDefault("payment_type") as string ?? "Penerimaan";
        var partyField = type == "Pengeluaran" ? "vendor" : "customer";
        var party = d.GetValueOrDefault(partyField) as string;
        var vType = OpenItems.VoucherTypeFor(type);
        var rows = (d.GetValueOrDefault("allocations") as IEnumerable<Dictionary<string, object?>> ?? []).ToList();
        var errors = new List<FieldError>();
        var allocs = new List<Alloc>();
        for (var i = 0; i < rows.Count; i++)
        {
            var p = $"allocations[{i}]";
            var inv = (rows[i].GetValueOrDefault("voucher_name") as string)?.Trim();
            var amt = Acc.Dec(rows[i].GetValueOrDefault("allocated_amount"));
            if (string.IsNullOrEmpty(inv)) { errors.Add(new($"{p}.voucher_name", "No. faktur wajib diisi")); continue; }
            if (amt <= 0) errors.Add(new($"{p}.allocated_amount", "Jumlah dialokasikan harus lebih dari 0"));
            if (allocs.Any(a => a.Invoice == inv)) errors.Add(new($"{p}.voucher_name", $"Faktur {inv} dialokasikan lebih dari sekali"));
            Document? invoice = null;
            try { invoice = await c.Docs.GetAsync(vType, inv, ct); }
            catch (DocumentNotFoundException) { errors.Add(new($"{p}.voucher_name", $"{(vType == "SalesInvoice" ? "Faktur penjualan" : "Faktur pembelian")} {inv} tidak ditemukan")); }
            if (invoice is not null)
            {
                if (invoice.DocStatus != DocStatus.Submitted) errors.Add(new($"{p}.voucher_name", $"Faktur {inv} belum di-submit atau sudah dibatalkan"));
                else if (!Equals(invoice.Data.GetValueOrDefault(partyField), party))
                    errors.Add(new($"{p}.voucher_name", $"Faktur {inv} bukan milik {(type == "Pengeluaran" ? "pemasok" : "pelanggan")} ini"));
                else
                {
                    var outstanding = await OpenItems.OutstandingAsync(ledger, c.Doc.TenantId, vType, inv, ct);
                    rows[i]["outstanding"] = outstanding;
                    if (amt > outstanding) errors.Add(new($"{p}.allocated_amount", $"Melebihi sisa tagihan {inv} ({outstanding:N2})"));
                }
            }
            allocs.Add(new(i, inv, amt));
        }
        var total = allocs.Sum(a => a.Amount);
        d["total_allocated"] = total;
        var paid = Acc.Dec(d.GetValueOrDefault("paid_amount"));
        if (allocs.Count > 0 && paid != total)
            errors.Add(new("paid_amount", $"Jumlah dibayar ({paid:N2}) harus sama dengan total dialokasikan ({total:N2}); selisih belum didukung"));
        if (errors.Count > 0) throw new ValidationFailedException(errors);
        return (type, party!, vType, allocs);
    }

    public async Task BeforeValidateAsync(HookContext c, CancellationToken ct) => await CheckAsync(c, ct);

    public async Task AfterSaveAsync(HookContext c, CancellationToken ct)
    {
        if (c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) return;
        var (type, party, vType, allocs) = await CheckAsync(c, ct);
        var date = Acc.Date(c.Doc.Data.GetValueOrDefault("posting_date"));
        var cash = (string)c.Doc.Data["payment_account"]!;
        var partyType = type == "Pengeluaran" ? "Vendor" : "Customer";
        var paid = allocs.Sum(a => a.Amount);
        if (type == "Penerimaan") c.AddLedger(new(date, cash, paid, 0));
        foreach (var a in allocs)
        {
            // Akun piutang/utang diambil dari baris faktur itu sendiri, agar pelunasan mengenai akun kontrol yang sama.
            var account = (await ledger.GetAgainstAsync(c.Doc.TenantId, vType, a.Invoice, ct)).First(e => e.VoucherName == a.Invoice).Account;
            c.AddLedger(type == "Penerimaan"
                ? new LedgerLine(date, account, 0, a.Amount, partyType, party, vType, a.Invoice)
                : new LedgerLine(date, account, a.Amount, 0, partyType, party, vType, a.Invoice));
        }
        if (type == "Pengeluaran") c.AddLedger(new(date, cash, 0, paid));
    }

    public Task OnCancelAsync(HookContext c, CancellationToken ct) => Acc.ReverseAsync(c, ledger, store, clock, ct);
}
