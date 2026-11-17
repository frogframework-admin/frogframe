<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Stock Opname (StockReconciliation)

Aturan yang dijalankan kode saat dokumen Stock Opname (StockReconciliation) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **StockReconciliationHook**: Stock Opname: qty & nilai sistem per item (dihitung ulang tiap simpan), selisih ke hitungan fisik; submit membukukan selisih nilai.
- **StockReconciliationFetchAction**: Aksi "Fetch items" stock opname: semua item yang punya saldo di gudang, qty hitung = qty sistem.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `items`: "{g.Key} is listed more than once" — terjadi bila `!(wh is null)` (StockReconciliationHook, StockHooks.cs:268)
- `items[{i}].new_rate`: "{item.Name} has no valuation rate yet; enter the cost per unit" — terjadi bila `r.Dec("counted_qty") > 0 && rate <= 0` (StockReconciliationHook, StockHooks.cs:282)

## Tombol aksi gagal: Saat tombol aksi diklik

- "Choose the warehouse first" — terjadi bila `wh is null` (StockReconciliationFetchAction, StockHooks.cs:329)

## Tombol aksi: Fetch items

Tombol **Fetch items** di form Stock Opname hanya tampil bila `doc.DocStatus == DocStatus.Draft` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Aksi "Fetch items" stock opname: semua item yang punya saldo di gudang, qty hitung = qty sistem.

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
- Stock: lihat `_shared-Stock.md`
- StockPlan: lihat `_shared-StockPlan.md`
