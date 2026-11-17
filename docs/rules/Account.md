<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Account (Account)

Aturan yang dijalankan kode saat dokumen Account (Account) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **AccountRulesHook**: Akun: kelompok sama dengan induk; kode tidak bisa diubah; akun yang sudah dipakai buku besar terkunci sifatnya dan tidak bisa dihapus; akun grup yang masih punya anak tidak bisa dijadikan akun posting.

## Validasi (dokumen ditolak): Pemeriksaan CheckParentAsync

- `root_type`: "The root type must match the parent account ({root})" — terjadi bila `!(parent is null) && root is not null && root != c.Doc.Data.Str("root_type")` (AccountRulesHook, AccountingHooks.cs:24)

## Validasi (dokumen ditolak): Saat hapus

- `name`: "Account {c.Doc.Name} is already used in the ledger and cannot be deleted. Deactivate it instead." — terjadi bila `await ledger.AccountUsedAsync(c.Doc.TenantId, c.Doc.Name, ct)` (AccountRulesHook, AccountingHooks.cs:50)
- `name`: "Account {c.Doc.Name} still has child accounts" — terjadi bila `await HasChildrenAsync(new Masters(store, c.Doc.TenantId), c.Doc.Name, ct)` (AccountRulesHook, AccountingHooks.cs:52)

## Validasi (dokumen ditolak): Saat perubahan disimpan

- `account_code`: "The account number cannot be changed after creation (number = document name)" — terjadi bila `!(c.Previous is null) && c.Previous.Data.Str("account_code") != c.Doc.Data.Str("account_code")` (AccountRulesHook, AccountingHooks.cs:38)
- `is_group`: "This account still has child accounts; move them first" — terjadi bila `!(c.Previous is null) && !(c.Previous.Data.Str("account_code") != c.Doc.Data.Str("account_code")) && c.Previous.Data.Bool("is_group") && !c.Doc.Data.Bool("is_group") && await HasChildrenAsync(m, c.Doc.Name, ct)` (AccountRulesHook, AccountingHooks.cs:40)
- `{f}`: "The account is already used in the ledger; root type, report type, normal balance, and group status cannot be changed" — terjadi bila `!(c.Previous is null) && !(c.Previous.Data.Str("account_code") != c.Doc.Data.Str("account_code")) && !(c.Previous.Data.Bool("is_group") && !c.Doc.Data.Bool("is_group") && await HasChildrenAsync(m, c.Doc.Name, ct)) && !Equals(c.Previous.Data.GetValueOrDefault(f), c.Doc.Data.GetValueOrDefault(f))` (AccountRulesHook, AccountingHooks.cs:44)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
