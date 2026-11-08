using Framework.Accounting;
using Framework.Documents;
using Framework.Metadata;
using Framework.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

/// <summary>Prasyarat & fondasi akuntansi: filter Link, Single DocType, buku besar append-only, periode, rantai hash.</summary>
sealed partial class Tests
{
    private const string JE = "JournalEntry";

    /// <summary>Bagan akun kecil + periode November 2026 (Terbuka) untuk tenant acme.</summary>
    private async Task SeedLedgerAsync()
    {
        var admin = U("admin");
        async Task A(string code, string name, string root, bool group, string? parent = null, bool active = true) =>
            await Docs.InsertAsync(admin, "Account", P(("code", code), ("account_name", name), ("root_type", root),
                ("balance_type", root is "Aset" or "Beban" ? "Debit" : "Kredit"), ("is_group", group), ("is_active", active), ("parent_account", parent)));
        await A("1000", "Aset", "Aset", true);
        await A("1100", "Kas", "Aset", false, "1000");
        await A("1900", "Kas lama", "Aset", false, "1000", active: false);
        await A("4000", "Pendapatan jasa", "Pendapatan", false);
        await A("5000", "Beban kantor", "Beban", false);
        await Docs.InsertAsync(admin, Ledger.PeriodDocType, P(("period_name", "2026-11"), ("start_date", "2026-11-01"), ("end_date", "2026-11-30"), ("status", "Terbuka")));
    }

    private static List<Dictionary<string, object?>> Lines(params (string Acc, decimal D, decimal K)[] l) =>
        l.Select(x => new Dictionary<string, object?> { ["account"] = x.Acc, ["debit"] = x.D, ["credit"] = x.K }).ToList();

    private async Task<Document> DraftJe(string date, params (string Acc, decimal D, decimal K)[] l) =>
        await Docs.InsertAsync(U("dewi"), JE, P(("posting_date", date), ("remarks", "uji"), ("items", Lines(l))));

    private ILedgerReader LedgerReader => sp.GetRequiredService<ILedgerReader>();

    [Test]
    public async Task Filter_link_diterapkan_di_pencarian_dan_ditegakkan_server()
    {
        await SeedLedgerAsync();
        var leaf = await Docs.SearchLinkAsync(U("dewi"), "Account", null, default, "JournalEntryItem", "account");
        Assert.True(leaf.Count > 0 && leaf.All(o => o.Name is not ("1000" or "1900")), "pencarian akun jurnal hanya akun daun yang aktif: " + string.Join(",", leaf.Select(o => o.Name)));
        var parents = await Docs.SearchLinkAsync(U("dewi"), "Account", null, default, "Account", "parent_account");
        Assert.True(parents.Select(o => o.Name).SequenceEqual(["1000"]), "induk akun hanya akun grup");

        var group = await Assert.Throws<ValidationFailedException>(() => DraftJe("2026-11-05", ("1000", 100, 0), ("4000", 0, 100)), "akun grup ditolak server");
        Assert.True(group.Errors.Any(e => e.Message.Contains("tidak memenuhi syarat")), string.Join("; ", group.Errors));
        await Assert.Throws<ValidationFailedException>(() => DraftJe("2026-11-05", ("1900", 100, 0), ("4000", 0, 100)), "akun nonaktif ditolak server");
    }

    [Test]
    public async Task Single_doctype_hanya_satu_per_tenant_dan_tidak_bisa_dihapus()
    {
        var admin = U("admin");
        var s1 = await Docs.InsertAsync(admin, "AccountingSettings", P());
        Assert.Equal("AccountingSettings", s1.Name, "nama dokumen tunggal = nama DocType");
        await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(admin, "AccountingSettings", P()), "dokumen kedua ditolak");
        await Assert.Throws<InvalidStateException>(() => Docs.DeleteAsync(admin, "AccountingSettings", s1.Name, s1.Version), "tidak bisa dihapus");
        var bad = MetadataJson.ParseDocType(MetadataJson.Serialize(sp.GetRequiredService<MetadataRegistry>().Current.Get("JournalEntryItem").Def));
        bad.Version++; bad.IsSingle = true;
        Assert.True(!sp.GetRequiredService<MetadataRegistry>().Preview(bad).Valid, "child table tidak bisa sekaligus Single");
    }

    [Test]
    public async Task Jurnal_submit_diposting_seimbang_dengan_rantai_hash_utuh()
    {
        await SeedLedgerAsync();
        var je = await DraftJe("2026-11-05", ("1100", 1_000_000m, 0), ("4000", 0, 1_000_000m));
        je = await Docs.RunActionAsync(U("dewi"), JE, je.Name, DocActions.Submit, je.Version, null);
        var gl = await LedgerReader.GetVoucherAsync("acme", JE, je.Name);
        Assert.True(gl.Count == 2 && gl.Sum(e => e.Debit) == gl.Sum(e => e.Credit) && gl.Sum(e => e.Debit) == 1_000_000m, "2 baris seimbang");
        Assert.True(gl.All(e => e.PeriodId == "2026-11" && e.VoucherVersion == je.Version), "periode & versi voucher tercatat");
        Assert.True(await LedgerReader.VerifyChainAsync("acme") is null, "rantai hash utuh");
        Assert.True(await LedgerReader.VerifyChainAsync("globex") is null, "tenant lain tidak terpengaruh");
    }

    [Test]
    public async Task Posting_gagal_tidak_mengubah_dokumen_dan_tidak_menulis_buku_besar()
    {
        await SeedLedgerAsync();
        var unbalanced = await DraftJe("2026-11-05", ("1100", 500m, 0), ("4000", 0, 400m));
        var e1 = await Assert.Throws<ValidationFailedException>(() => Docs.RunActionAsync(U("dewi"), JE, unbalanced.Name, DocActions.Submit, unbalanced.Version, null), "tidak seimbang");
        Assert.True(e1.Errors.Any(e => e.Message.Contains("tidak seimbang")), string.Join("; ", e1.Errors));
        Assert.True((await Docs.GetAsync(U("dewi"), JE, unbalanced.Name)).DocStatus == DocStatus.Draft, "dokumen tetap Draft (atomik)");
        Assert.True((await LedgerReader.GetVoucherAsync("acme", JE, unbalanced.Name)).Count == 0, "tidak ada baris buku besar");

        var outside = await DraftJe("2026-12-03", ("1100", 100m, 0), ("4000", 0, 100m));
        var e2 = await Assert.Throws<ValidationFailedException>(() => Docs.RunActionAsync(U("dewi"), JE, outside.Name, DocActions.Submit, outside.Version, null), "di luar periode");
        Assert.True(e2.Errors.Any(e => e.Message.Contains("periode akuntansi yang Terbuka")), string.Join("; ", e2.Errors));
        Assert.True((await Docs.GetAsync(U("dewi"), JE, outside.Name)).DocStatus == DocStatus.Draft, "tetap Draft");
    }

    [Test]
    public async Task Pembatalan_membuat_pembalik_dan_akun_terpakai_terlindungi()
    {
        await SeedLedgerAsync();
        var je = await DraftJe("2026-11-10", ("5000", 250_000m, 0), ("1100", 0, 250_000m));
        je = await Docs.RunActionAsync(U("dewi"), JE, je.Name, DocActions.Submit, je.Version, null);
        je = await Docs.RunActionAsync(U("dewi"), JE, je.Name, DocActions.Cancel, je.Version, null);
        var gl = await LedgerReader.GetVoucherAsync("acme", JE, je.Name);
        Assert.True(gl.Count == 4 && gl.Count(e => e.ReversalOf is not null) == 2, "2 baris asli + 2 pembalik");
        Assert.True(gl.GroupBy(e => e.Account).All(g => g.Sum(e => e.Debit) == g.Sum(e => e.Credit)), "saldo per akun kembali nol");
        Assert.True(gl.Where(e => e.ReversalOf is not null).All(e => e.PostingDate == new DateOnly(2026, 11, 10)), "periode asli masih Terbuka: tanggal pembalik = tanggal asli");
        Assert.True(await LedgerReader.VerifyChainAsync("acme") is null, "rantai tetap utuh");

        var kas = await Docs.GetAsync(U("admin"), "Account", "1100");
        var del = await Assert.Throws<ValidationFailedException>(() => Docs.DeleteAsync(U("admin"), "Account", "1100", kas.Version), "akun terpakai tidak bisa dihapus");
        Assert.True(del.Errors.Any(e => e.Message.Contains("sudah dipakai di buku besar")), "pesan jelas untuk pengguna");
        await Assert.Throws<ValidationFailedException>(() => Docs.UpdateAsync(U("admin"), "Account", "1100", kas.Version, P(("root_type", "Beban"))), "tipe akun terpakai tidak bisa diubah");
    }

    [Test]
    public async Task Periode_tidak_boleh_tumpang_tindih()
    {
        await SeedLedgerAsync();
        var e = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(U("admin"), Ledger.PeriodDocType,
            P(("period_name", "2026-12"), ("start_date", "2026-11-20"), ("end_date", "2026-12-31"), ("status", "Terbuka"))), "tumpang tindih");
        Assert.True(e.Errors.Any(x => x.Message.Contains("tumpang tindih")), string.Join("; ", e.Errors));
        await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(U("admin"), Ledger.PeriodDocType,
            P(("period_name", "2027-01"), ("start_date", "2027-01-31"), ("end_date", "2027-01-01"))), "mulai setelah akhir");
    }

    [Test]
    public async Task Rantai_hash_mendeteksi_perubahan_dan_penghapusan_di_penyimpanan()
    {
        if (sp.GetRequiredService<IDocumentStore>() is not InMemoryDocumentStore mem) return; // versi PostgreSQL: test Pg_*
        await SeedLedgerAsync();
        var a = await DraftJe("2026-11-05", ("1100", 100m, 0), ("4000", 0, 100m));
        a = await Docs.RunActionAsync(U("dewi"), JE, a.Name, DocActions.Submit, a.Version, null);
        var b = await DraftJe("2026-11-06", ("1100", 200m, 0), ("4000", 0, 200m));
        b = await Docs.RunActionAsync(U("dewi"), JE, b.Name, DocActions.Submit, b.Version, null);

        mem.TamperForTest(e => e.VoucherName == a.Name && e.Debit > 0, e => e with { Debit = 999m });
        var broken = await LedgerReader.VerifyChainAsync("acme");
        Assert.True(broken is not null && broken.Reason.Contains("entry_hash"), "nilai yang diubah terdeteksi: " + broken);

        mem.TamperForTest(e => e.VoucherName == a.Name && e.Debit == 999m, e => e with { Debit = 100m }); // kembalikan
        Assert.True(await LedgerReader.VerifyChainAsync("acme") is null, "dikembalikan → utuh lagi");
        mem.TamperForTest(e => e.VoucherName == a.Name, _ => null); // hapus satu voucher utuh
        var gap = await LedgerReader.VerifyChainAsync("acme");
        Assert.True(gap is not null && gap.Seq == 1, "voucher yang dihapus utuh terdeteksi: " + gap);
    }

    [Test, PgTest]
    public async Task Pg_buku_besar_menolak_update_delete_dan_insert_tidak_seimbang()
    {
        await SeedLedgerAsync();
        var je = await DraftJe("2026-11-05", ("1100", 300m, 0), ("4000", 0, 300m));
        await Docs.RunActionAsync(U("dewi"), JE, je.Name, DocActions.Submit, je.Version, null);
        var db = sp.GetRequiredService<NpgsqlDataSource>();
        async Task<string> Fail(string sql)
        {
            try { await using var c = db.CreateCommand(sql); await c.ExecuteNonQueryAsync(); return "TIDAK DITOLAK"; }
            catch (PostgresException ex) { return ex.MessageText; }
        }
        Assert.True((await Fail("UPDATE frogframe_gl_entries SET debit = debit + 1 WHERE debit > 0")).Contains("append-only"), "UPDATE ditolak trigger");
        Assert.True((await Fail("DELETE FROM frogframe_gl_entries")).Contains("append-only"), "DELETE ditolak trigger");
        Assert.True((await Fail("TRUNCATE frogframe_gl_entries")).Contains("append-only"), "TRUNCATE ditolak trigger");
        var oneSided = await Fail("INSERT INTO frogframe_gl_entries (id, tenant_id, seq, posting_date, period_id, account_id, debit, credit, voucher_type, voucher_name, voucher_version, line_number, prev_hash, entry_hash, created_by) " +
            "VALUES (gen_random_uuid(), 'acme', 999, '2026-11-05', '2026-11', '1100', 50, 0, 'Manual', 'X-1', 1, 1, repeat('0',64), repeat('1',64), 'sql')");
        Assert.True(oneSided.Contains("tidak seimbang"), "insert langsung tidak seimbang ditolak saat COMMIT: " + oneSided);
        Assert.True(await LedgerReader.VerifyChainAsync("acme") is null, "rantai tetap utuh setelah upaya manipulasi yang ditolak");

        // Pemilik tabel bisa mematikan trigger (lapis 2 = pemisahan peran); rantai hash tetap mendeteksinya.
        await using (var c = db.CreateCommand("ALTER TABLE frogframe_gl_entries DISABLE TRIGGER trg_gl_no_update_delete; " +
            "UPDATE frogframe_gl_entries SET remarks = 'diubah' WHERE seq = 1; ALTER TABLE frogframe_gl_entries ENABLE TRIGGER trg_gl_no_update_delete;"))
            await c.ExecuteNonQueryAsync();
        var broken = await LedgerReader.VerifyChainAsync("acme");
        Assert.True(broken is not null && broken.Seq == 1, "perubahan oleh pemilik tabel terdeteksi rantai hash: " + broken);
    }
}

/// <summary>Q15: akun kontrol piutang/utang terkunci dari Jurnal Umum; bisa dilonggarkan per akun lewat flag.</summary>
sealed partial class Tests
{
    [Test]
    public async Task Akun_kontrol_ditolak_di_jurnal_umum_dan_wajib_untuk_piutang_default()
    {
        await SeedLedgerAsync();
        var admin = U("admin");
        await Docs.InsertAsync(admin, "Account", P(("code", "1300"), ("account_name", "Piutang usaha"), ("root_type", "Aset"),
            ("balance_type", "Debit"), ("is_group", false), ("is_active", true), ("parent_account", "1000"), ("is_control_account", true)));

        var e = await Assert.Throws<ValidationFailedException>(() => DraftJe("2026-11-05", ("1300", 100m, 0), ("4000", 0, 100m)), "akun kontrol ditolak");
        Assert.True(e.Errors.Any(x => x.Path == "items[0].account" && x.Message.Contains("akun kontrol")), string.Join("; ", e.Errors));

        // Dilonggarkan per akun: flag dimatikan → jurnal boleh (perubahan flag tercatat di audit akun).
        var piutang = await Docs.GetAsync(admin, "Account", "1300");
        await Docs.UpdateAsync(admin, "Account", "1300", piutang.Version, P(("is_control_account", false)));
        var ok = await DraftJe("2026-11-05", ("1300", 100m, 0), ("4000", 0, 100m));
        Assert.True(ok.DocStatus == DocStatus.Draft, "setelah flag dimatikan, jurnal diterima");

        var bad = await Assert.Throws<ValidationFailedException>(() => Docs.InsertAsync(admin, "AccountingSettings", P(("default_receivable_account", "1300"))),
            "piutang default harus akun kontrol");
        Assert.True(bad.Errors.Any(x => x.Path == "default_receivable_account"), string.Join("; ", bad.Errors));
    }
}
