<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Lead (Lead)

Aturan yang dijalankan kode saat dokumen Lead (Lead) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **LeadHook**: Lead: peringatan duplikat saat dibuat; hasil konversi (pelanggan, kontak, opportunity) diisi dari dokumen yang menunjuk lead ini.
- **LeadConvertAction**: Aksi "Convert to customer" (Salesforce Lead Convert): Customer (+ lead), Contact (tertaut ke Customer), Opportunity opsional, lalu Lead → Converted. Berjalan sebagai user yang menekan tombol (izin & scope Sales berlaku). Framework tidak punya transaksi multi-dokumen dari form action, jadi bila gagal di tengah dipakai kompensasi: dokumen yang sudah dibuat dihapus lagi (urutan terbalik, sebagai user sistem). Bila penghapusan itu sendiri gagal, pesan menyebutkan dokumen yang tertinggal.

## Validasi (dokumen ditolak): Saat tombol aksi diklik

- `name`: "Set the default company (General Settings) and sales stages" — terjadi bila `company is null || stage is null` (LeadConvertAction, CrmHooks.cs:123)

## Tombol aksi gagal: Saat tombol aksi diklik

- "Set 'Customer group for converted leads' in CRM Settings" — terjadi bila `group is null` (LeadConvertAction, CrmHooks.cs:94)
- "Conversion failed: {ex.Message}.{tail}" (LeadConvertAction, CrmHooks.cs:142)

## Peringatan (dokumen tetap tersimpan): Saat dokumen baru disimpan

- "Possible duplicate: {docType} {dup.Name} has the same {field} ({value})" — terjadi bila `!(mode == "No") && (await m.QueryAsync(docType, [new FilterClause(field, "=", value)], ct, take: 1)).FirstOrDefault() is { } dup` (LeadHook, CrmHooks.cs:55)

## Tombol aksi: Convert to customer

Tombol **Convert to customer** di form Lead hanya tampil bila `doc.Data.Str("status") is not (Crm.Converted or "Unqualified") && (user.HasRole("Sales") || user.HasRole("Manager"))` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Aksi "Convert to customer" (Salesforce Lead Convert): Customer (+ lead), Contact (tertaut ke Customer), Opportunity opsional, lalu Lead → Converted. Berjalan sebagai user yang menekan tombol (izin & scope Sales berlaku). Framework tidak punya transaksi multi-dokumen dari form action, jadi bila gagal di tengah dipakai kompensasi: dokumen yang sudah dibuat dihapus lagi (urutan terbalik, sebagai user sistem). Bila penghapusan itu sendiri gagal, pesan menyebutkan dokumen yang tertinggal.

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
