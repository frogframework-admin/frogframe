<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Sales Invoice (SalesInvoice)

Aturan yang dijalankan kode saat dokumen Sales Invoice (SalesInvoice) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **SalesInvoiceSalesRepHook**: Faktur penjualan: sales rep (dasar komisi) dari sales order → penawaran → account owner pelanggan; sales order harus milik pelanggan yang sama.
- **SalesStockHook**: Faktur & nota penjualan dengan Update stock: barang keluar (faktur) / kembali (nota) pada rata-rata bergerak gudang; Dr HPP / Cr persediaan (nota: kebalikannya). Pembalikan saat batal ikut jurnal faktur (Reversal hook faktur).

## Validasi (dokumen ditolak): Pemeriksaan PrepareAsync

- `warehouse`: "Choose the warehouse for 'Update stock'" — terjadi bila `d.Str("warehouse") is null` (SalesStockHook, StockHooks.cs:419)

## Validasi (dokumen ditolak): Saat cancel

- `name`: "{c.Doc.Name} has payments or credit notes. Cancel those documents first." — terjadi bila `await OpenItems.HasSettlementAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct)` (SalesInvoiceHook, InvoiceHooks.cs:300)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `sales_order`: "{so.Name} belongs to another customer" — terjadi bila `so.Data.Str("customer") != d.Str("customer")` (SalesInvoiceSalesRepHook, CrmExtraHooks.cs:95)

## Aturan bersama yang juga berlaku

- Accounts: lihat `_shared-Accounts.md`
- BudgetControl: lihat `_shared-BudgetControl.md`
- InvoiceCore: lihat `_shared-InvoiceCore.md`
- Masters: lihat `_shared-Masters.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
- Rates: lihat `_shared-Rates.md`
- Stock: lihat `_shared-Stock.md`
- StockPlan: lihat `_shared-StockPlan.md`
