<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama Accounts

Dipakai oleh: EmployeeAdvance, ExpenseClaim, JournalEntry, PaymentEntry, PurchaseDebitNote, PurchaseInvoice, SalesCreditNote, SalesInvoice.

## Validasi (dokumen ditolak)

- `{path}`: "Account {acc.Name} is a group account" — terjadi bila `acc.Data.Bool("is_group")` (Accounts, Common.cs:437)
- `{path}`: "Account {acc.Name} is inactive" — terjadi bila `acc.Data.TryGetValue("is_active", out var active) && active is false` (Accounts, Common.cs:438)
- `{path}`: "Account {acc.Name} is frozen" — terjadi bila `acc.Data.Bool("is_frozen") && !user.IsAdmin` (Accounts, Common.cs:439)
