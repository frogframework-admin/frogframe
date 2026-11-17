<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Journal Entry (JournalEntry)

Aturan yang dijalankan kode saat dokumen Journal Entry (JournalEntry) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **JournalEntryHook**: Jurnal Umum. Simpan: isi kurs & nama akun, validasi akun kontrol/pihak/cost center. Submit: posting baris dalam mata uang fungsional (base_debit/base_credit hasil compute metadata). Akun kontrol hanya untuk "Opening Balance" dan menjadi open item (against = jurnal ini) sehingga bisa dilunasi Payment Entry. Cancel: ditolak bila open item-nya sudah dilunasi; selain itu baris pembalik.

## Validasi (dokumen ditolak): Saat cancel

- `name`: "Opening balances in {c.Doc.Name} have been settled; cancel the payments first" — terjadi bila `await OpenItems.HasSettlementAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct)` (JournalEntryHook, AccountingHooks.cs:200)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `accounts`: "A journal needs at least 2 lines" — terjadi bila `rows.Count < 2` (JournalEntryHook, AccountingHooks.cs:142)
- `{p}.account`: "Account {acc.Name} is a control account; use invoices/payments, or an Opening Entry journal" — terjadi bila `isControl && !isOpening` (JournalEntryHook, AccountingHooks.cs:155)
- `{p}.party`: "Account {acc.Name} requires a party" — terjadi bila `(isControl || type is "Receivable" or "Payable") && row.Str("party") is null` (JournalEntryHook, AccountingHooks.cs:157)
- `{p}.party_type`: "A receivable account cannot be used for a supplier" — terjadi bila `type == "Receivable" && row.Str("party_type") is "Vendor"` (JournalEntryHook, AccountingHooks.cs:159)
- `{p}.party_type`: "A payable account cannot be used for a customer" — terjadi bila `type == "Payable" && row.Str("party_type") is "Customer"` (JournalEntryHook, AccountingHooks.cs:161)
- `{p}.cost_center`: "Account {acc.Name} requires a cost center" — terjadi bila `needsCc && row.Str("cost_center") is null` (JournalEntryHook, AccountingHooks.cs:165)

## Validasi (dokumen ditolak): Setelah simpan/submit (posting, efek ke dokumen lain)

- `posting_date`: "The posting date is required" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft)` (JournalEntryHook, AccountingHooks.cs:175)

## Aturan bersama yang juga berlaku

- Accounts: lihat `_shared-Accounts.md`
- BudgetControl: lihat `_shared-BudgetControl.md`
- Masters: lihat `_shared-Masters.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
- Rates: lihat `_shared-Rates.md`
