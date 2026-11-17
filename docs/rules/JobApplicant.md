<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Job Applicant (JobApplicant)

Aturan yang dijalankan kode saat dokumen Job Applicant (JobApplicant) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **JobApplicantHook**: Pelamar: requisition harus terbuka; email unik per requisition; status Hired hanya lewat aksi 'Create employee'.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `job_requisition`: "{req.Name} is not open" — terjadi bila `c.Previous is null && req.DocStatus != DocStatus.Submitted` (JobApplicantHook, RecruitmentHooks.cs:61)
- `email`: "{email} already applied to {req.Name} ({dup.Name})" — terjadi bila `dup is not null` (JobApplicantHook, RecruitmentHooks.cs:66)
- `status`: "'Hired' is set by 'Create employee' on an accepted job offer" — terjadi bila `d.Str("status") == "Hired" && d.Str("employee") is null && !c.User.IsAdmin` (JobApplicantHook, RecruitmentHooks.cs:68)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
