<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Purchase Order (PurchaseOrder)

Aturan yang dijalankan kode saat dokumen Purchase Order (PurchaseOrder) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **PurchaseOrderHook**: Purchase Order: dari purchase request yang disetujui; termin & PPN masukan default; status terima & tagih dihitung saat dibuka.
- **PurchaseOrderReceiveAction**: Aksi "Receive" pada PO: draft goods receipt berisi semua sisa qty.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `purchase_request`: "{pr.Name} is {pr.WorkflowState}, not Approved" — terjadi bila `c.Doc.DocStatus == DocStatus.Draft && pr.WorkflowState != Approval.Approved` (PurchaseOrderHook, ProcurementHooks.cs:86)

## Tombol aksi gagal: Saat tombol aksi diklik

- "Goods receipt creation failed: {ex.Message}" (PurchaseOrderReceiveAction, ProcurementHooks.cs:234)

## Peringatan (dokumen tetap tersimpan): Saat simpan/submit (validasi)

- "Supplier {vendor.Name} is on hold: invoices can be recorded but payments are blocked" — terjadi bila `vendor.Data.Bool("on_hold")` (PurchaseOrderHook, ProcurementHooks.cs:101)

## Tombol aksi: Receive

Tombol **Receive** di form Purchase Order hanya tampil bila `doc.DocStatus == DocStatus.Submitted && doc.Data.Str("receipt_status") != "Fully received" && (user.HasRole("Purchasing") || user.HasRole("Stock") || user.IsAdmin)` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Aksi "Receive" pada PO: draft goods receipt berisi semua sisa qty.

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
- Stock: lihat `_shared-Stock.md`
