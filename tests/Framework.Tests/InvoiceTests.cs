using Framework.Accounting;
using Framework.Documents;
using Framework.Metadata;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Faktur penjualan & pembelian: perhitungan V1.3 (Q1/Q2/Q4/K1/K2), posting, pembalik, validasi.</summary>
sealed partial class Tests
{
    private const string SI = "SalesInvoice", PI = "PurchaseInvoice";

    private async Task<string> SeedInvoicingAsync()
    {
        await SeedLedgerAsync();
        var admin = U("admin");
        async Task A(string code, string name, string root, string bal, bool control = false) =>
            await Docs.InsertAsync(admin, "Account", P(("code", code), ("account_name", name), ("root_type", root), ("balance_type", bal),
                ("is_group", false), ("is_active", true), ("is_control_account", control)));
        await A("1300", "Piutang usaha", "Aset", "Debit", control: true);
        await A("1400", "PPN masukan", "Aset", "Debit");
        await A("2100", "Utang usaha", "Kewajiban", "Kredit", control: true);
        await A("2200", "PPN keluaran", "Kewajiban", "Kredit");
        await A("4100", "Potongan penjualan", "Pendapatan", "Debit");
        await Docs.InsertAsync(admin, "TaxRate", P(("tax_code", "PPN_STD"), ("tax_name", "PPN standar (uji)"), ("rate", 11m),
            ("tax_base_factor", 1m), ("rounding", "Bulat ke bawah (Rupiah)"), ("output_account", "2200"), ("input_account", "1400")));
        await Docs.InsertAsync(admin, "AccountingSettings", P(("default_receivable_account", "1300"), ("default_payable_account", "2100"),
            ("default_income_account", "4000"), ("default_expense_account", "5000"), ("default_sales_discount_account", "4100")));
        await Docs.InsertAsync(admin, "Customer", P(("code", "C001"), ("customer_name", "PT Pelanggan Uji")));
        return (await Docs.InsertAsync(admin, "Vendor", P(("vendor_name", "PT Pemasok Uji"), ("category", "Jasa")))).Name;
    }

    private static Dictionary<string, object?> Line(string desc, decimal qty, decimal rate, decimal discPct = 0, decimal discAmt = 0, string? tax = "PPN_STD") =>
        new() { ["item_description"] = desc, ["qty"] = qty, ["rate"] = rate, ["discount_percent"] = discPct, ["discount_amount"] = discAmt, ["tax_rate"] = tax };

    private async Task<Document> Submit(string dt, Document d) => await Docs.RunActionAsync(U("dewi"), dt, d.Name, DocActions.Submit, d.Version, null);

    [Test]
    public async Task Faktur_penjualan_dihitung_server_dengan_ppn_dibulatkan_ke_bawah_per_tarif()
    {
        await SeedInvoicingAsync();
        // Baris 1: 3 × 33.333,33 = 99.999,99 (bruto), diskon 10% = 10.000,00 → 89.999,99
        // Baris 2: 1,5 × 20.000 = 30.000, tanpa diskon
        // DPP = 119.999,99; PPN 11% = 13.199,9989 → bulat ke bawah = 13.199; total = 133.198,99
        var si = await Docs.InsertAsync(U("dewi"), SI, P(("customer", "C001"), ("posting_date", "2026-11-12"),
            ("items", new List<Dictionary<string, object?>> { Line("Jasa A", 3m, 33_333.33m, discPct: 10m), Line("Jasa B", 1.5m, 20_000m) }),
            ("grand_total", 1m), ("tax_amount", 999_999m))); // nilai klien diabaikan
        Assert.Equal(129_999.99m, (decimal)si.Data["gross_total"]!, "bruto");
        Assert.Equal(10_000.00m, (decimal)si.Data["discount_total"]!, "diskon 10% dari 99.999,99");
        Assert.Equal(119_999.99m, (decimal)si.Data["subtotal"]!, "DPP setelah diskon");
        Assert.Equal(13_199m, (decimal)si.Data["tax_amount"]!, "PPN dibulatkan ke bawah ke Rupiah (sekali per tarif)");
        Assert.Equal(133_198.99m, (decimal)si.Data["grand_total"]!, "total tagihan dihitung server");
    }

    [Test]
    public async Task Faktur_penjualan_diposting_gross_dengan_akun_potongan_dan_dibalik_saat_batal()
    {
        await SeedInvoicingAsync();
        var si = await Docs.InsertAsync(U("dewi"), SI, P(("customer", "C001"), ("posting_date", "2026-11-12"),
            ("items", new List<Dictionary<string, object?>> { Line("Jasa", 2m, 50_000m, discAmt: 10_000m) })));
        si = await Submit(SI, si);
        var gl = await LedgerReader.GetVoucherAsync("acme", SI, si.Name);
        decimal D(string a) => gl.Where(e => e.Account == a).Sum(e => e.Debit);
        decimal K(string a) => gl.Where(e => e.Account == a).Sum(e => e.Credit);
        // bruto 100.000, diskon 10.000 → DPP 90.000, PPN floor(9.900) = 9.900, total 99.900
        Assert.True(D("1300") == 99_900m && D("4100") == 10_000m && K("4000") == 100_000m && K("2200") == 9_900m,
            $"Dr piutang 99.900, Dr potongan 10.000, Cr pendapatan bruto 100.000, Cr PPN 9.900 — {string.Join(" | ", gl.Select(e => $"{e.Account} {e.Debit}/{e.Credit}"))}");
        var piutang = gl.Single(e => e.Account == "1300");
        Assert.True(piutang.PartyType == "Customer" && piutang.Party == "C001" && piutang.AgainstVoucherName == si.Name, "baris piutang menyimpan mitra & faktur (untuk pelunasan)");

        si = await Docs.RunActionAsync(U("dewi"), SI, si.Name, DocActions.Cancel, si.Version, null);
        gl = await LedgerReader.GetVoucherAsync("acme", SI, si.Name);
        Assert.True(gl.GroupBy(e => e.Account).All(g => g.Sum(e => e.Debit) == g.Sum(e => e.Credit)), "setelah batal, semua akun kembali nol");
        Assert.True(await LedgerReader.VerifyChainAsync("acme") is null, "rantai utuh");
    }

    [Test]
    public async Task Faktur_pembelian_diposting_net_dan_nomor_pemasok_tidak_boleh_ganda()
    {
        var vendor = await SeedInvoicingAsync();
        var pi = await Docs.InsertAsync(U("dewi"), PI, P(("vendor", vendor), ("supplier_invoice_no", "INV-77"), ("posting_date", "2026-11-15"),
            ("items", new List<Dictionary<string, object?>> { Line("Sewa", 1m, 1_000_000m, discPct: 5m) })));
        pi = await Submit(PI, pi);
        var gl = await LedgerReader.GetVoucherAsync("acme", PI, pi.Name);
        // bruto 1.000.000, diskon 5% → beban NET 950.000; PPN floor(104.500) = 104.500; utang 1.054.500
        Assert.True(gl.Single(e => e.Account == "5000").Debit == 950_000m, "beban dicatat bersih setelah diskon (K2)");
        Assert.True(gl.Single(e => e.Account == "1400").Debit == 104_500m && gl.Single(e => e.Account == "2100").Credit == 1_054_500m, "PPN masukan & utang");
        Assert.True(!gl.Any(e => e.Account == "4100"), "tidak ada akun potongan untuk pembelian");

        var dup = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(U("dewi"), PI, P(("vendor", vendor), ("supplier_invoice_no", "INV-77"),
            ("posting_date", "2026-11-16"), ("items", new List<Dictionary<string, object?>> { Line("Lagi", 1m, 10m) }))), "faktur pemasok ganda");
        Assert.True(dup.Errors.Any(e => e.Message.Contains(pi.Name)), "pesan menyebut faktur yang sudah ada");
    }

    [Test]
    public async Task Faktur_negatif_diskon_berlebih_dan_pengaturan_kosong_ditolak()
    {
        await SeedLedgerAsync();
        await Docs.InsertAsync(U("admin"), "Customer", P(("code", "C002"), ("customer_name", "Tanpa Pengaturan")));
        var neg = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(U("dewi"), SI, P(("customer", "C002"), ("posting_date", "2026-11-12"),
            ("items", new List<Dictionary<string, object?>> { Line("Retur?", -1m, 10_000m, tax: null) }))), "qty negatif");
        Assert.True(neg.Errors.Any(e => e.Message.Contains("nota kredit")), string.Join("; ", neg.Errors));
        await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(U("dewi"), SI, P(("customer", "C002"), ("posting_date", "2026-11-12"),
            ("items", new List<Dictionary<string, object?>> { Line("X", 1m, 10_000m, discAmt: 20_000m, tax: null) }))), "diskon melebihi baris");
        var draft = await Docs.InsertAsync(U("dewi"), SI, P(("customer", "C002"), ("posting_date", "2026-11-12"),
            ("items", new List<Dictionary<string, object?>> { Line("Y", 1m, 10_000m, tax: null) })));
        var noSettings = await Assert.Throws<ValidationFailedException>(() => Submit(SI, draft), "tanpa Pengaturan Akuntansi");
        Assert.True(noSettings.Errors.Any(e => e.Message.Contains("Pengaturan Akuntansi")), string.Join("; ", noSettings.Errors));
        Assert.True((await Docs.GetAsync(U("dewi"), SI, draft.Name)).DocStatus == DocStatus.Draft, "tetap Draft");
    }

    [Test]
    public Task Pembulatan_pajak_mengikuti_master()
    {
        var floor = new Apps.Sample.Hooks.TaxInfo("A", 11m, 1m, true, "x", "y");
        var half = floor with { FloorRupiah = false };
        Assert.Equal(1_099m, Apps.Sample.Hooks.InvoiceMath.Tax(9_999.99m, floor), "floor: 1.099,9989 → 1.099");
        Assert.Equal(1_100.00m, Apps.Sample.Hooks.InvoiceMath.Tax(9_999.99m, half), "half-up 2 desimal: 1.099,9989 → 1.100,00");
        Assert.Equal(1_100m, Apps.Sample.Hooks.InvoiceMath.Tax(10_000m, floor with { Rate = 12m, Factor = 11m / 12m }), "faktor dasar pengenaan: 12% × 11/12 = 11% efektif dari 10.000 = 1.100");
        return Task.CompletedTask;
    }
}
