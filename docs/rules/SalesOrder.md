<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Sales Order (SalesOrder)

Aturan yang dijalankan kode saat dokumen Sales Order (SalesOrder) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **SalesOrderHook**: Sales Order: isi dari penawaran Accepted, harga dari daftar harga, PPN default; status penagihan dihitung saat dibuka.
- **SalesOrderMakeInvoiceAction**: Aksi "Create Sales Invoice" dari sales order (sisa yang belum ditagih ditagih penuh; ubah qty di draft untuk tagihan sebagian).

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `quotation`: "{q.Name} is {q.WorkflowState}; only an accepted quotation becomes an order" — terjadi bila `c.Doc.DocStatus == DocStatus.Draft && q.WorkflowState is not ("Accepted" or "Billed")` (SalesOrderHook, CrmExtraHooks.cs:55)
- `quotation`: "This quotation is still for a lead; convert the lead to a customer first" — terjadi bila `q.Data.Str("customer") is null` (SalesOrderHook, CrmExtraHooks.cs:56)

## Tombol aksi gagal: Saat tombol aksi diklik

- "Invoice creation failed: {ex.Message}" (SalesOrderMakeInvoiceAction, CrmExtraHooks.cs:159)

## Tombol aksi: Create Sales Invoice

Tombol **Create Sales Invoice** di form Sales Order hanya tampil bila `doc.DocStatus == DocStatus.Submitted && doc.Data.Str("billing_status") != "Fully billed" && (user.HasRole("Finance") || user.IsAdmin)` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Aksi "Create Sales Invoice" dari sales order (sisa yang belum ditagih ditagih penuh; ubah qty di draft untuk tagihan sebagian).

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
