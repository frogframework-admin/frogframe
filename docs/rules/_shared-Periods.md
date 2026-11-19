<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama Periods

Dipakai oleh: AssetDisposal, CommissionStatement, DepreciationRun, EmployeeAdvance, ExpenseClaim, GoodsReceipt, JournalEntry, PaymentEntry, PaymentReconciliation, PayrollRun, PeriodClosingVoucher, PurchaseDebitNote, PurchaseInvoice, SalesCreditNote, SalesInvoice, StockEntry, StockReconciliation.

## Validasi (dokumen ditolak)

- `{path}`: "No accounting period exists for {D.Iso(date)}" — terjadi bila `period is null` (Periods, Common.cs:214)
- `{path}`: "Period {period.Name} is closed" — terjadi bila `period.Data.Str("status") != "Open"` (Periods, Common.cs:215)
- `{path}`: "Period {period.Name} is locked for this transaction (subledger close)" — terjadi bila `lockField is not null && period.Data.Bool(lockField) && !user.IsAdmin` (Periods, Common.cs:217)
- `{path}`: "Books are frozen until {D.Iso(frozen)}" — terjadi bila `settings.Date("books_frozen_until") is { } frozen && date <= frozen && !user.IsAdmin` (Periods, Common.cs:221)
- `{path}`: "The posting date cannot be in the future (see Accounting Settings)" — terjadi bila `date > today && !(await m.SingleAsync("AccountingSettings", ct)).Bool("allow_future_posting")` (Periods, Common.cs:227)
