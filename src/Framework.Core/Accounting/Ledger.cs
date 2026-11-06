using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Framework.Documents;

namespace Framework.Accounting;

/// <summary>
/// Satu baris buku besar yang diajukan hook (mis. saat dokumen di-submit). Identitas voucher (DocType, nama, versi)
/// diisi otomatis dari dokumen yang sedang disimpan. Baris ditulis dalam TRANSAKSI YANG SAMA dengan dokumen.
/// </summary>
public sealed record LedgerLine(
    DateOnly PostingDate, string Account, decimal Debit, decimal Credit,
    string? PartyType = null, string? Party = null,
    string? AgainstVoucherType = null, string? AgainstVoucherName = null,
    string? Department = null, string? Remarks = null, Guid? ReversalOf = null);

/// <summary>Baris buku besar yang sudah tersimpan (append-only).</summary>
public sealed record LedgerEntry(
    Guid Id, string TenantId, long Seq, DateOnly PostingDate, string PeriodId, string Account, decimal Debit, decimal Credit,
    string VoucherType, string VoucherName, int VoucherVersion, int LineNumber,
    string? PartyType, string? Party, string? AgainstVoucherType, string? AgainstVoucherName,
    string? Department, string? Remarks, Guid? ReversalOf, string PrevHash, string EntryHash, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>Hasil verifikasi rantai hash: null bila utuh, atau seq pertama yang tidak cocok beserta alasannya.</summary>
public sealed record ChainBreak(long Seq, string Reason);

public interface ILedgerReader
{
    Task<IReadOnlyList<LedgerEntry>> GetVoucherAsync(string tenantId, string voucherType, string voucherName, CancellationToken ct = default);
    Task<ChainBreak?> VerifyChainAsync(string tenantId, CancellationToken ct = default);
    /// <summary>Apakah akun sudah pernah menerima posting (akun seperti itu tidak boleh dihapus atau diubah kodenya).</summary>
    Task<bool> AccountUsedAsync(string tenantId, string account, CancellationToken ct = default);
    /// <summary>Semua baris yang merujuk faktur ini sebagai against voucher (baris piutang/utang faktur + pelunasannya).</summary>
    Task<IReadOnlyList<LedgerEntry>> GetAgainstAsync(string tenantId, string againstType, string againstName, CancellationToken ct = default);
}

public sealed class LedgerException(string message) : Exception(message);

public static class Ledger
{
    public const string PeriodDocType = "AccountingPeriod";
    public const string AccountDocType = "Account";
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>Validasi yang tidak bergantung pada data: satu sisi, nilai positif, 2 desimal, seimbang per voucher.</summary>
    public static void ValidateShape(IReadOnlyList<(Document Voucher, LedgerLine Line)> lines)
    {
        var errors = new List<FieldError>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i].Line;
            var p = $"ledger[{i}]";
            if (string.IsNullOrWhiteSpace(l.Account)) errors.Add(new(p, "Akun wajib diisi"));
            if (l.Debit < 0 || l.Credit < 0) errors.Add(new(p, "Debit dan kredit tidak boleh negatif"));
            if ((l.Debit > 0) == (l.Credit > 0)) errors.Add(new(p, "Setiap baris harus berisi debit ATAU kredit (tidak boleh keduanya atau kosong)"));
            if (decimal.Round(l.Debit, 2) != l.Debit || decimal.Round(l.Credit, 2) != l.Credit) errors.Add(new(p, "Nilai maksimal 2 desimal"));
        }
        foreach (var g in lines.GroupBy(x => (x.Voucher.DocType, x.Voucher.Name, x.Voucher.Version)))
        {
            var d = g.Sum(x => x.Line.Debit); var c = g.Sum(x => x.Line.Credit);
            if (d != c) errors.Add(new("ledger", $"Jurnal {g.Key.DocType} {g.Key.Name} tidak seimbang: debit {d:N2} ≠ kredit {c:N2}"));
        }
        if (errors.Count > 0) throw new ValidationFailedException(errors);
    }

    /// <summary>Isi kanonik baris untuk hash. Urutan dan format tetap; jangan diubah tanpa migrasi rantai.</summary>
    public static string Canonical(string tenantId, long seq, DateOnly date, string period, string account, decimal debit, decimal credit,
        string voucherType, string voucherName, int voucherVersion, int lineNumber, string? partyType, string? party,
        string? againstType, string? againstName, string? department, string? remarks, Guid? reversalOf) =>
        string.Join('\u001f', tenantId, seq.ToString(CultureInfo.InvariantCulture), date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            period, account, debit.ToString("F2", CultureInfo.InvariantCulture), credit.ToString("F2", CultureInfo.InvariantCulture),
            voucherType, voucherName, voucherVersion.ToString(CultureInfo.InvariantCulture), lineNumber.ToString(CultureInfo.InvariantCulture),
            partyType ?? "", party ?? "", againstType ?? "", againstName ?? "", department ?? "", remarks ?? "", reversalOf?.ToString() ?? "");

    public static string Hash(string prevHash, string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prevHash + "|" + canonical))).ToLowerInvariant();

    public static string CanonicalOf(LedgerEntry e) => Canonical(e.TenantId, e.Seq, e.PostingDate, e.PeriodId, e.Account, e.Debit, e.Credit,
        e.VoucherType, e.VoucherName, e.VoucherVersion, e.LineNumber, e.PartyType, e.Party, e.AgainstVoucherType, e.AgainstVoucherName,
        e.Department, e.Remarks, e.ReversalOf);

    /// <summary>Verifikasi rantai dari daftar baris urut seq (dipakai store in-memory; PostgreSQL memakai logika yang sama).</summary>
    public static ChainBreak? Verify(IEnumerable<LedgerEntry> ordered)
    {
        var prev = Genesis; long expectSeq = 1;
        foreach (var e in ordered)
        {
            if (e.Seq != expectSeq) return new(expectSeq, $"Nomor urut hilang: diharapkan {expectSeq}, ditemukan {e.Seq}");
            if (e.PrevHash != prev) return new(e.Seq, "prev_hash tidak cocok dengan baris sebelumnya");
            if (Hash(prev, CanonicalOf(e)) != e.EntryHash) return new(e.Seq, "Isi baris berubah (entry_hash tidak cocok)");
            prev = e.EntryHash; expectSeq++;
        }
        return null;
    }

    /// <summary>
    /// Saldo terbuka faktur tidak boleh berbalik tanda (mis. piutang jadi negatif karena kelebihan bayar).
    /// Dipanggil store di dalam transaksi, di bawah kunci tenant, setelah baris baru ditambahkan.
    /// </summary>
    public static string? OpenItemViolation(IEnumerable<(string AgainstType, string AgainstName, string Account, string VoucherName, decimal Debit, decimal Credit)> lines,
        IEnumerable<(string AgainstType, string AgainstName, string Account)> touched)
    {
        var all = lines.ToList();
        foreach (var key in touched.Distinct())
        {
            var rows = all.Where(l => l.AgainstType == key.AgainstType && l.AgainstName == key.AgainstName && l.Account == key.Account).ToList();
            var own = rows.Where(r => r.VoucherName == key.AgainstName).Sum(r => r.Debit - r.Credit);
            var total = rows.Sum(r => r.Debit - r.Credit);
            if (own > 0 && total < 0 || own < 0 && total > 0 || own == 0 && total != 0)
                return $"Pembayaran melebihi sisa tagihan {key.AgainstType} {key.AgainstName}";
        }
        return null;
    }

    /// <summary>Baris pembalik untuk satu voucher: debit/kredit ditukar dan menunjuk baris aslinya.</summary>
    public static IReadOnlyList<LedgerLine> Reversal(IEnumerable<LedgerEntry> original, DateOnly postingDate) =>
        original.Where(e => e.ReversalOf is null)
            .Select(e => new LedgerLine(postingDate, e.Account, e.Credit, e.Debit, e.PartyType, e.Party, e.AgainstVoucherType, e.AgainstVoucherName,
                e.Department, $"Pembalikan {e.VoucherType} {e.VoucherName}", e.Id)).ToList();
}
