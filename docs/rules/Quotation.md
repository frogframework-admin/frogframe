<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Quotation (Quotation)

Aturan yang dijalankan kode saat dokumen Quotation (Quotation) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **QuotationMakeSalesOrderAction**: Aksi "Create sales order" dari penawaran Accepted.
- **QuotationHook**: Penawaran: data dari opportunity, masa berlaku default, harga dari daftar harga, PPN default; diterima → opportunity Menang.
- **QuotationMakeInvoiceAction**: Aksi "Buat Faktur" dari penawaran yang diterima (role Finance, karena Sales tidak membuat faktur).

## Tombol aksi gagal: Saat tombol aksi diklik

- "Sales order creation failed: {ex.Message}" (QuotationMakeSalesOrderAction, CrmExtraHooks.cs:126)
- "This quotation is still for a lead; convert the lead to a customer first" — terjadi bila `q.Str("customer") is null` (QuotationMakeInvoiceAction, CrmHooks.cs:249)
- "Invoice creation failed: {ex.Message}" (QuotationMakeInvoiceAction, CrmHooks.cs:269)

## Tombol aksi: Create sales order

Tombol **Create sales order** di form Quotation hanya tampil bila `doc.WorkflowState == "Accepted" && (user.HasRole("Sales") || user.HasRole("Manager"))` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Aksi "Create sales order" dari penawaran Accepted.

## Tombol aksi: Create Sales Invoice

Tombol **Create Sales Invoice** di form Quotation hanya tampil bila `doc.WorkflowState == "Accepted" && user.HasRole("Finance")` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Aksi "Buat Faktur" dari penawaran yang diterima (role Finance, karena Sales tidak membuat faktur).

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
