<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Purchase Invoice (PurchaseInvoice)

Aturan yang dijalankan kode saat dokumen Purchase Invoice (PurchaseInvoice) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **PurchaseInvoicePoHook**: Purchase Invoice dengan PO: pemasok harus sama, baris disalin dari PO bila kosong; saat submit dicek 3-way match (total tagihan ≤ nilai diterima).
- **PurchaseStockHook**: Faktur pembelian & item stok. Update stock: baris stok dibukukan ke akun persediaan gudang (barang masuk pada nilai baris × kurs). Dengan PO tanpa Update stock: baris stok ke akun barang diterima belum ditagih (penerimaannya lewat Goods Receipt). Nota debit (retur ke pemasok) tidak mengubah stok: keluarkan barang lewat Stock Entry Material Issue. Didaftarkan sebelum PurchaseInvoiceHook agar akun baris sudah terisi saat hook faktur melengkapi akun lainnya.

## Validasi (dokumen ditolak): Saat cancel

- `name`: "{c.Doc.Name} has payments or debit notes. Cancel those documents first." — terjadi bila `await OpenItems.HasSettlementAsync(ledger, c.Doc.TenantId, c.Doc.DocType, c.Doc.Name, ct)` (PurchaseInvoiceHook, InvoiceHooks.cs:344)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `purchase_order`: "{po.Name} belongs to another supplier" — terjadi bila `!(await m.GetAsync("PurchaseOrder", d.Str("purchase_order"), ct) is not { } po) && po.Data.Str("vendor") != d.Str("vendor")` (PurchaseInvoicePoHook, ProcurementHooks.cs:150)
- `warehouse`: "Choose the warehouse for 'Update stock'" — terjadi bila `d.Str("warehouse") is null` (PurchaseStockHook, StockHooks.cs:484)
- `update_stock`: "{po} is already received with goods receipts; untick 'Update stock' so the invoice clears 'stock received not billed'" — terjadi bila `!(d.Str("warehouse") is null) && d.Str("purchase_order") is { } po && (await m.QueryAsync("GoodsReceipt", [new FilterClause("purchase_order", "=", po)], ct, take: 50)) .Any(g => g.DocStatus == DocStatus.Submitted && g.Data.Rows("items").Any(r => r.Bool("is_stock_item")))` (PurchaseStockHook, StockHooks.cs:487)

## Validasi (dokumen ditolak): Setelah simpan/submit (posting, efek ke dokumen lain)

- `purchase_order`: "3-way match: {po.Name} billed {billed} exceeds the received value {received}; record the goods receipt first" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && !(await m.GetAsync("PurchaseOrder", c.Doc.Data.Str("purchase_order"), ct) is not { } po) && !(mode == "Off") && !(billed <= received + 1m) && mode == "Stop"` (PurchaseInvoicePoHook, ProcurementHooks.cs:173)
- `update_stock`: "Debit notes do not move stock; issue the returned goods with a Material Issue stock entry" — terjadi bila `!(d.Str("warehouse") is null) && !(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft || !c.Doc.Data.Bool("update_stock")) && !((await m.GetAsync("Item", r.Str("item"), ct))?.Data.Bool("is_stock_item") != true)` (PurchaseStockHook, StockHooks.cs:526)

## Peringatan (dokumen tetap tersimpan): Saat simpan/submit (validasi)

- "{item.Name} is a stock item but this invoice does not update stock; it is booked to {r.Str("expense_account") ?? "the expense account"}" (PurchaseStockHook, StockHooks.cs:497)

## Peringatan (dokumen tetap tersimpan): Setelah simpan/submit (posting, efek ke dokumen lain)

- "3-way match: {po.Name} billed {billed} exceeds the received value {received}" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && !(await m.GetAsync("PurchaseOrder", c.Doc.Data.Str("purchase_order"), ct) is not { } po) && !(mode == "Off") && !(billed <= received + 1m) && !(mode == "Stop")` (PurchaseInvoicePoHook, ProcurementHooks.cs:174)

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
