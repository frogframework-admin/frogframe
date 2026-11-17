<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama PostgresLedgerExtensionHandler

Dipakai oleh: semua dokumen yang memposting ke buku besar.

## Validasi (dokumen ditolak)

- "Tanggal posting {date} tidak berada di periode akuntansi yang Terbuka (Open)" — terjadi bila `await q.ExecuteScalarAsync(ct) as string tidak ditemukan/kosong` (PostgresLedgerExtensionHandler, PostgresLedgerExtensionHandler.cs:58)
- "Akun '{account}' tidak ditemukan" — terjadi bila `!found.TryGetValue(account, out var info)` (PostgresLedgerExtensionHandler, PostgresLedgerExtensionHandler.cs:72)
- "Akun '{account}' adalah akun induk dan tidak bisa menerima posting" — terjadi bila `info.Group == "true"` (PostgresLedgerExtensionHandler, PostgresLedgerExtensionHandler.cs:73)
- "Akun '{account}' nonaktif" — terjadi bila `info.Active == "false"` (PostgresLedgerExtensionHandler, PostgresLedgerExtensionHandler.cs:74)
