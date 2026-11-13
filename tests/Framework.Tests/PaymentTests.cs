using Framework.Persistence;
using Framework.Accounting;
using Framework.Documents;
using Framework.Metadata;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Pembayaran: alokasi ke faktur, sisa tagihan dari GL, penjaga konkurensi di store, larangan batal faktur yang dibayar.</summary>
sealed partial class Tests
{
    private const string PAY = "PaymentEntry";

    private async Task<(string Vendor, Document Si)> SeedPaymentsAsync()
    {
        var vendor = await SeedInvoicingAsync();
        await Docs.InsertAsync(U("admin"), "Account", P(("code", "1110"), ("account_name", "Bank"), ("root_type", "Aset"), ("balance_type", "Debit"),
            ("is_group", false), ("is_active", true), ("parent_account", "1000"), ("is_cash_or_bank", true)));
        // bruto 100.000, diskon 10.000, PPN 9.900 → total 99.900
        var si = await Docs.InsertAsync(U("dewi"), SI, P(("customer", "C001"), ("posting_date", "2026-11-12"),
            ("items", new List<Dictionary<string, object?>> { Line("Jasa", 2m, 50_000m, discAmt: 10_000m) })));
        return (vendor, await Submit(SI, si));
    }

    private async Task<Document> DraftPay(string invoice, decimal amount, decimal? paid = null, string type = "Penerimaan", string? party = "C001", string? vendor = null) =>
        await Docs.InsertAsync(U("dewi"), PAY, P(("payment_type", type), ("customer", type == "Penerimaan" ? party : null), ("vendor", vendor),
            ("posting_date", "2026-11-20"), ("payment_account", "1110"), ("paid_amount", paid ?? amount),
            ("allocations", new List<Dictionary<string, object?>> { new() { ["voucher_name"] = invoice, ["allocated_amount"] = amount } })));

    private async Task<decimal> Outstanding(string type, string name) =>
        await Framework.Tests.TestHooks.OpenItems.OutstandingAsync(LedgerReader, "acme", type, name, default);

    [Test]
    public async Task Pembayaran_bertahap_melunasi_faktur_dan_sisa_dari_buku_besar()
    {
        var (_, si) = await SeedPaymentsAsync();
        Assert.Equal(99_900m, await Outstanding(SI, si.Name), "sisa awal = total tagihan");
        var p1 = await Submit(PAY, await DraftPay(si.Name, 40_000m));
        Assert.Equal(59_900m, await Outstanding(SI, si.Name), "sisa setelah 40.000");
        var gl = await LedgerReader.GetVoucherAsync("acme", PAY, p1.Name);
        Assert.True(gl.Single(e => e.Account == "1110").Debit == 40_000m && gl.Single(e => e.Account == "1300").Credit == 40_000m, "Dr bank, Cr piutang");
        Assert.True(gl.Single(e => e.Account == "1300").AgainstVoucherName == si.Name, "pelunasan merujuk faktur");
        await Submit(PAY, await DraftPay(si.Name, 59_900m));
        Assert.Equal(0m, await Outstanding(SI, si.Name), "lunas");
        Assert.True(await LedgerReader.VerifyChainAsync("acme") is null, "rantai utuh");
    }

    [Test]
    public async Task Pembayaran_divalidasi_sisa_selisih_mitra_dan_status_faktur()
    {
        var (_, si) = await SeedPaymentsAsync();
        string Msg(ValidationFailedException e) => string.Join("; ", e.Errors.Select(x => x.Message));
        var over = await Assert.Throws<ValidationFailedException>(() => DraftPay(si.Name, 100_000m), "melebihi sisa");
        Assert.True(Msg(over).Contains("Melebihi sisa tagihan"), Msg(over));
        var diff = await Assert.Throws<ValidationFailedException>(() => DraftPay(si.Name, 10_000m, paid: 12_000m), "selisih (Q7)");
        Assert.True(Msg(diff).Contains("harus sama dengan total dialokasikan"), Msg(diff));
        await Docs.InsertAsync(U("admin"), "Customer", P(("code", "C009"), ("customer_name", "Pelanggan lain")));
        var other = await Assert.Throws<ValidationFailedException>(() => DraftPay(si.Name, 10_000m, party: "C009"), "mitra lain");
        Assert.True(Msg(other).Contains("bukan milik"), Msg(other));
        var draft = await Docs.InsertAsync(U("dewi"), SI, P(("customer", "C001"), ("posting_date", "2026-11-12"),
            ("items", new List<Dictionary<string, object?>> { Line("Draft", 1m, 1_000m) })));
        var notSubmitted = await Assert.Throws<ValidationFailedException>(() => DraftPay(draft.Name, 100m), "faktur draft");
        Assert.True(Msg(notSubmitted).Contains("belum di-submit"), Msg(notSubmitted));
        var dup = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(U("dewi"), PAY, P(("payment_type", "Penerimaan"), ("customer", "C001"),
            ("posting_date", "2026-11-20"), ("payment_account", "1110"), ("paid_amount", 2m),
            ("allocations", new List<Dictionary<string, object?>> { new() { ["voucher_name"] = si.Name, ["allocated_amount"] = 1m }, new() { ["voucher_name"] = si.Name, ["allocated_amount"] = 1m } }))), "faktur ganda");
        Assert.True(Msg(dup).Contains("lebih dari sekali"), Msg(dup));
    }

    [Test]
    public async Task Dua_pembayaran_bersamaan_tidak_melebihi_sisa_tagihan()
    {
        var (_, si) = await SeedPaymentsAsync();
        var a = await DraftPay(si.Name, 60_000m);
        var b = await DraftPay(si.Name, 60_000m); // masing-masing ≤ sisa saat disimpan
        var results = await Task.WhenAll(new[] { a, b }.Select(async d =>
        {
            try { await Submit(PAY, d); return "ok"; }
            catch (ValidationFailedException e) { return string.Join("; ", e.Errors.Select(x => x.Message)); }
        }));
        Assert.True(results.Count(r => r == "ok") == 1, "tepat satu berhasil: " + string.Join(" | ", results));
        Assert.True(results.Single(r => r != "ok").Contains("sisa tagihan"), "yang lain ditolak karena sisa tagihan: " + results.Single(r => r != "ok"));
        Assert.Equal(39_900m, await Outstanding(SI, si.Name), "sisa tidak pernah negatif");
    }

    [Test]
    public async Task Faktur_yang_sudah_dibayar_tidak_bisa_dibatalkan_sebelum_pembayaran_dibatalkan()
    {
        var (_, si) = await SeedPaymentsAsync();
        var pay = await Submit(PAY, await DraftPay(si.Name, 50_000m));
        var blocked = await Assert.Throws<ValidationFailedException>(() => Docs.RunActionAsync(U("dewi"), SI, si.Name, DocActions.Cancel, si.Version, null), "faktur dibayar");
        Assert.True(blocked.Errors.Any(e => e.Message.Contains("Batalkan pembayarannya")), "pesan jelas");
        await Docs.RunActionAsync(U("dewi"), PAY, pay.Name, DocActions.Cancel, pay.Version, null);
        Assert.Equal(99_900m, await Outstanding(SI, si.Name), "pembayaran dibatalkan → sisa kembali penuh");
        var current = await Docs.GetAsync(U("dewi"), SI, si.Name);
        await Docs.RunActionAsync(U("dewi"), SI, si.Name, DocActions.Cancel, current.Version, null);
        Assert.Equal(0m, await Outstanding(SI, si.Name), "faktur dibatalkan");
    }

    [Test]
    public async Task Pembayaran_ke_pemasok_mendebit_utang_dan_mengkredit_bank()
    {
        var (vendor, _) = await SeedPaymentsAsync();
        var pi = await Docs.InsertAsync(U("dewi"), PI, P(("vendor", vendor), ("supplier_invoice_no", "INV-90"), ("posting_date", "2026-11-15"),
            ("items", new List<Dictionary<string, object?>> { Line("Sewa", 1m, 100_000m, tax: null) })));
        pi = await Submit(PI, pi);
        Assert.Equal(100_000m, await Outstanding(PI, pi.Name), "sisa utang");
        var pay = await Submit(PAY, await DraftPay(pi.Name, 100_000m, type: "Pengeluaran", party: null, vendor: vendor));
        var gl = await LedgerReader.GetVoucherAsync("acme", PAY, pay.Name);
        Assert.True(gl.Single(e => e.Account == "2100").Debit == 100_000m && gl.Single(e => e.Account == "1110").Credit == 100_000m, "Dr utang, Cr bank");
        Assert.Equal(0m, await Outstanding(PI, pi.Name), "utang lunas");
    }
}

sealed partial class Tests
{
    [Test]
    public async Task Penjaga_sisa_tagihan_di_store_bekerja_tanpa_hook()
    {
        var (_, si) = await SeedPaymentsAsync();
        // Voucher buatan yang langsung ke store (melewati hook pembayaran): pelunasan 200.000 untuk faktur 99.900.
        var fake = new Document { TenantId = "acme", DocType = "PaymentEntry", Name = "PAY-MANUAL-1", Owner = "dewi", ModifiedBy = "dewi", Version = 1,
            Created = DateTimeOffset.UtcNow, Modified = DateTimeOffset.UtcNow, SchemaVersion = 1, Data = new() };
        var uow = new UnitOfWork();
        uow.Ledger.Add((fake, new LedgerLine(new DateOnly(2026, 11, 20), "1110", 200_000m, 0)));
        uow.Ledger.Add((fake, new LedgerLine(new DateOnly(2026, 11, 20), "1300", 0, 200_000m, "Customer", "C001", SI, si.Name)));
        var store = sp.GetRequiredService<Framework.Persistence.IDocumentStore>();
        var ex = await Assert.Throws<LedgerException>(() => store.CommitAsync(uow), "store menolak pelunasan berlebih");
        Assert.True(ex.Message.Contains("sisa tagihan"), ex.Message);
        Assert.Equal(99_900m, await Outstanding(SI, si.Name), "tidak ada yang tersimpan");
    }
}
