<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama StockPlan

Dipakai oleh: GoodsReceipt, PurchaseDebitNote, PurchaseInvoice, SalesCreditNote, SalesInvoice, StockEntry, StockReconciliation.

Perencana valuasi untuk satu dokumen yang sedang di-submit: cek stok cukup & tanggal mundur, hitung nilai keluar rata-rata bergerak.

## Validasi (dokumen ditolak)

- `{path}`: "{item} in {wh} already has stock movements on {D.Iso(b.LastDate)}; stock postings before that date are not allowed" — terjadi bila `!(b.LastDate <= _date)` (StockPlan, StockHooks.cs:150)
- `{path}`: "Only {b.Qty} of {item} available in {wh}" — terjadi bila `qty > b.Qty && !_allowNegative` (StockPlan, StockHooks.cs:159)
