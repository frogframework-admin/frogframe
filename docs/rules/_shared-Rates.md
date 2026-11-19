<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama Rates

Dipakai oleh: GoodsReceipt, JournalEntry, PaymentEntry, PurchaseDebitNote, PurchaseInvoice, SalesCreditNote, SalesInvoice.

## Validasi (dokumen ditolak)

- `exchange_rate`: "No {rateType} exchange rate {from}→{to} for {day}. Add a Currency Exchange Rate or type the rate manually." — terjadi bila `!(from == to) && !(await Find(from, to) is { } direct) && !(await Find(to, from) is { } inverse)` (Rates, Common.cs:183)
