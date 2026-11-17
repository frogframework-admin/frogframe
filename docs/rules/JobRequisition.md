<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Job Requisition (JobRequisition)

Aturan yang dijalankan kode saat dokumen Job Requisition (JobRequisition) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **JobRequisitionHook**: Requisition: data jabatan dari posisi (harus Vacant saat draft); status, jumlah pelamar & yang diterima dihitung saat dibuka.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `position`: "Position {pos.Name} is {pos.Data.Str("status")}, not Vacant" — terjadi bila `c.Doc.DocStatus == DocStatus.Draft && pos.Data.Str("status") != "Vacant"` (JobRequisitionHook, RecruitmentHooks.cs:23)

## Peringatan (dokumen tetap tersimpan): Saat simpan/submit (validasi)

- "The salary range exceeds the maximum of grade {grade.Name} ({max})" — terjadi bila `max > 0 && d.Dec("salary_max") > max` (JobRequisitionHook, RecruitmentHooks.cs:32)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
