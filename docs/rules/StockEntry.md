<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Stock Entry (StockEntry)

Aturan yang dijalankan kode saat dokumen Stock Entry (StockEntry) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **StockEntryHook**: Stock Entry: penerimaan, pengeluaran, transfer. Submit: valuasi + jurnal persediaan.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `{p}.s_warehouse`: "Set the source warehouse" — terjadi bila `purpose != "Material Receipt" && s is null` (StockEntryHook, StockHooks.cs:201)
- `{p}.t_warehouse`: "Set the target warehouse" — terjadi bila `purpose != "Material Issue" && t is null` (StockEntryHook, StockHooks.cs:202)
- `{p}.t_warehouse`: "Source and target warehouses must differ" — terjadi bila `purpose == "Material Transfer" && s == t` (StockEntryHook, StockHooks.cs:203)
- `{p}.basic_rate`: "Enter the cost per unit for a receipt" — terjadi bila `purpose == "Material Receipt" && r.Dec("basic_rate") <= 0` (StockEntryHook, StockHooks.cs:204)

## Aturan bersama yang juga berlaku

- BudgetControl: lihat `_shared-BudgetControl.md`
- Masters: lihat `_shared-Masters.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
- Stock: lihat `_shared-Stock.md`
- StockPlan: lihat `_shared-StockPlan.md`
