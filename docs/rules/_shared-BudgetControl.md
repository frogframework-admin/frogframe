<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama BudgetControl

Dipakai oleh: CommissionStatement, ExpenseClaim, JournalEntry, PayrollRun, PurchaseDebitNote, PurchaseInvoice, SalesCreditNote, SalesInvoice, StockEntry.

Kontrol anggaran saat posting beban: untuk tiap (akun, cost center) yang bertambah di sisi debit, realisasi tahun buku (dari buku besar) + posting ini dibandingkan dengan anggaran yang di-submit (seluruh tahun, atau year-to-date = tahunan × bulan berjalan / 12). Warn = pesan, Stop = ditolak. Dipanggil dari posting faktur pembelian/penjualan, jurnal, klaim biaya, dan payroll.

## Validasi (dokumen ditolak)

- `name`: "Budget {b.Name} exceeded for {account}{(cc is null ? "" : $" / {cc}")}: actual {actual} + this {amount} = {actual + amount} > {(ytd ? "year-to-date budget" : "budget")} {limit}" — terjadi bila `!(row is null) && !(cc is not null && cc != dept) && !(actual + amount <= limit) && b.Data.Str("action_if_exceeded") == "Stop"` (BudgetControl, BudgetHooks.cs:50)

## Peringatan

- "Budget {b.Name} exceeded for {account}{(cc is null ? "" : $" / {cc}")}: actual {actual} + this {amount} = {actual + amount} > {(ytd ? "year-to-date budget" : "budget")} {limit}" — terjadi bila `!(row is null) && !(cc is not null && cc != dept) && !(actual + amount <= limit) && !(b.Data.Str("action_if_exceeded") == "Stop")` (BudgetControl, BudgetHooks.cs:51)
