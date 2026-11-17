<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama Posting

Dipakai oleh: AssetDisposal, CommissionStatement, DepreciationRun, EmployeeAdvance, ExpenseClaim, GoodsReceipt, JournalEntry, PaymentEntry, PaymentReconciliation, PayrollRun, PeriodClosingVoucher, PurchaseDebitNote, PurchaseInvoice, SalesCreditNote, SalesInvoice, StockEntry, StockReconciliation.

Penyusun baris buku besar dalam mata uang fungsional. Menggabungkan baris berkunci sama, membuang nol, dan menyeimbangkan selisih pembulatan/kurs ke akun yang ditentukan. Dimensi: cost center dikirim sebagai LedgerLine.Department sampai LedgerLine punya dimensi generik.

## Validasi (dokumen ditolak)

- `name`: "Entry is out of balance by {diff} (exceeds the {what} tolerance of {tolerance})" — terjadi bila `!(diff == 0) && Math.Abs(diff) > tolerance` (Posting, Common.cs:252)
- `company`: "There is a {what} difference of {diff}; set the {what} account on the Company" — terjadi bila `!(diff == 0) && account is null` (Posting, Common.cs:254)
- `name`: "A posting needs at least 2 lines" — terjadi bila `_lines.Count < 2` (Posting, Common.cs:262)
