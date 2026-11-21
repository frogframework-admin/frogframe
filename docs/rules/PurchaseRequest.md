<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Purchase Request (PurchaseRequest)

Aturan yang dijalankan kode saat dokumen Purchase Request (PurchaseRequest) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **PurchaseRequestHook**: Purchase Request: perusahaan & cost center dari pemohon; status pemesanan dihitung saat dibuka.
- **PurchaseRequestMakeOrderAction**: Aksi "Create purchase order" dari purchase request yang disetujui (role Purchasing).

## Tombol aksi gagal: Saat tombol aksi diklik

- "Choose the suggested supplier on the request first, or create the order manually" — terjadi bila `ctx.Doc.Data.Str("suggested_vendor") is not { } vendor` (PurchaseRequestMakeOrderAction, ProcurementHooks.cs:197)
- "Purchase order creation failed: {ex.Message}" (PurchaseRequestMakeOrderAction, ProcurementHooks.cs:209)

## Tombol aksi: Create purchase order

Tombol **Create purchase order** di form Purchase Request hanya tampil bila `doc.WorkflowState == Approval.Approved && user.HasRole(ErpRoles.ProcurementBuyer)` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Aksi "Create purchase order" dari purchase request yang disetujui (role Purchasing).

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
