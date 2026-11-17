<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama LedgerException

Dipakai oleh: semua dokumen yang memposting ke buku besar.

## Validasi (dokumen ditolak)

- `{p}`: "Akun wajib diisi" — terjadi bila `string.IsNullOrWhiteSpace(l.Account)` (LedgerException, Ledger.cs:64)
- `{p}`: "Debit dan kredit tidak boleh negatif" — terjadi bila `l.Debit < 0 || l.Credit < 0` (LedgerException, Ledger.cs:65)
- `{p}`: "Setiap baris harus berisi debit ATAU kredit (tidak boleh keduanya atau kosong)" — terjadi bila `(l.Debit > 0) == (l.Credit > 0)` (LedgerException, Ledger.cs:66)
- `{p}`: "Nilai maksimal 2 desimal" — terjadi bila `decimal.Round(l.Debit, 2) != l.Debit || decimal.Round(l.Credit, 2) != l.Credit` (LedgerException, Ledger.cs:67)
- `ledger`: "Jurnal {g.Key.DocType} {g.Key.Name} tidak seimbang: debit {d} ≠ kredit {c}" — terjadi bila `d != c` (LedgerException, Ledger.cs:72)
