<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Purchase Debit Note (PurchaseDebitNote)

Aturan yang dijalankan kode saat dokumen Purchase Debit Note (PurchaseDebitNote) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **PurchaseStockHook**: Faktur pembelian & item stok. Update stock: baris stok dibukukan ke akun persediaan gudang (barang masuk pada nilai baris × kurs). Dengan PO tanpa Update stock: baris stok ke akun barang diterima belum ditagih (penerimaannya lewat Goods Receipt). Nota debit (retur ke pemasok) tidak mengubah stok: keluarkan barang lewat Stock Entry Material Issue. Didaftarkan sebelum PurchaseInvoiceHook agar akun baris sudah terisi saat hook faktur melengkapi akun lainnya.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `warehouse`: "Choose the warehouse for 'Update stock'" — terjadi bila `d.Str("warehouse") is null` (PurchaseStockHook, StockHooks.cs:484)
- `update_stock`: "{po} is already received with goods receipts; untick 'Update stock' so the invoice clears 'stock received not billed'" — terjadi bila `!(d.Str("warehouse") is null) && d.Str("purchase_order") is { } po && (await m.QueryAsync("GoodsReceipt", [new FilterClause("purchase_order", "=", po)], ct, take: 50)) .Any(g => g.DocStatus == DocStatus.Submitted && g.Data.Rows("items").Any(r => r.Bool("is_stock_item")))` (PurchaseStockHook, StockHooks.cs:487)

## Validasi (dokumen ditolak): Setelah simpan/submit (posting, efek ke dokumen lain)

- `update_stock`: "Debit notes do not move stock; issue the returned goods with a Material Issue stock entry" — terjadi bila `!(d.Str("warehouse") is null) && !(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft || !c.Doc.Data.Bool("update_stock")) && !((await m.GetAsync("Item", r.Str("item"), ct))?.Data.Bool("is_stock_item") != true)` (PurchaseStockHook, StockHooks.cs:526)

## Peringatan (dokumen tetap tersimpan): Saat simpan/submit (validasi)

- "{item.Name} is a stock item but this invoice does not update stock; it is booked to {r.Str("expense_account") ?? "the expense account"}" (PurchaseStockHook, StockHooks.cs:497)

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
