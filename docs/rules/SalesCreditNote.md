<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Sales Credit Note (SalesCreditNote)

Aturan yang dijalankan kode saat dokumen Sales Credit Note (SalesCreditNote) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **SalesStockHook**: Faktur & nota penjualan dengan Update stock: barang keluar (faktur) / kembali (nota) pada rata-rata bergerak gudang; Dr HPP / Cr persediaan (nota: kebalikannya). Pembalikan saat batal ikut jurnal faktur (Reversal hook faktur).

## Validasi (dokumen ditolak): Pemeriksaan PrepareAsync

- `warehouse`: "Choose the warehouse for 'Update stock'" — terjadi bila `d.Str("warehouse") is null` (SalesStockHook, StockHooks.cs:419)

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
