<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Goods Receipt (GoodsReceipt)

Aturan yang dijalankan kode saat dokumen Goods Receipt (GoodsReceipt) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **GoodsReceiptHook**: Goods Receipt: baris dari sisa PO bila kosong; qty diterima tidak boleh melebihi sisa per baris.
- **GoodsReceiptStockHook**: Goods Receipt untuk item stok: masuk gudang pada harga netto PO (dikonversi ke mata uang fungsional); Dr persediaan / Cr barang diterima belum ditagih.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `purchase_order`: "{po.Name} is not submitted or has been cancelled" — terjadi bila `po.DocStatus != DocStatus.Submitted` (GoodsReceiptHook, ProcurementHooks.cs:119)
- `items`: "Everything on {po.Name} has been received" — terjadi bila `rows.Count == 0` (GoodsReceiptHook, ProcurementHooks.cs:128)
- `items[{k}].po_line`: "{po.Name} has no line {line}" — terjadi bila `line < 1 || line > lines.Count` (GoodsReceiptHook, ProcurementHooks.cs:132)
- `items[{k}].received_qty`: "Line {line}: only {Pending(line)} not yet received" — terjadi bila `r.Dec("received_qty") > Pending(line)` (GoodsReceiptHook, ProcurementHooks.cs:136)
- `warehouse`: "Choose the warehouse that receives the stock items" — terjadi bila `!(await m.GetAsync("PurchaseOrder", d.Str("purchase_order"), ct) is not { } po) && !(line < 1 || line > lines.Count) && anyStock && d.Str("warehouse") is null` (GoodsReceiptStockHook, StockHooks.cs:361)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
- Rates: lihat `_shared-Rates.md`
- Stock: lihat `_shared-Stock.md`
- StockPlan: lihat `_shared-StockPlan.md`
