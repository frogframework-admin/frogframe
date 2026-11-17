<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Job Offer (JobOffer)

Aturan yang dijalankan kode saat dokumen Job Offer (JobOffer) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **JobOfferHook**: Penawaran: data jabatan dari requisition; satu penawaran aktif per pelamar; karyawan hasilnya dibaca saat dibuka.
- **JobOfferCreateEmployeeAction**: Aksi "Create employee" (Workday Hire): dari penawaran Accepted dibuat Employee (data pelamar + jabatan penawaran), lalu Salary Assignment gaji pokok bila komponennya diatur di Payroll Settings, dan pelamar ditandai Hired. Employee dibuat sebagai user HR (izin berlaku).

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `job_applicant`: "{applicant.Name} already has an active offer {other.Name}" — terjadi bila `other is not null` (JobOfferHook, RecruitmentHooks.cs:93)

## Tombol aksi gagal: Saat tombol aksi diklik

- "The applicant no longer exists" — terjadi bila `applicant is null` (JobOfferCreateEmployeeAction, RecruitmentHooks.cs:118)
- "The offer has no employee number" — terjadi bila `!(applicant is null) && number is null` (JobOfferCreateEmployeeAction, RecruitmentHooks.cs:121)
- "Fill gender and date of birth on applicant {applicant.Name} first" — terjadi bila `!(applicant is null) && !(number is null) && a.Str("gender") is null || a.Str("date_of_birth") is null` (JobOfferCreateEmployeeAction, RecruitmentHooks.cs:123)
- "Creating the employee failed: {ex.Message}" (JobOfferCreateEmployeeAction, RecruitmentHooks.cs:158)

## Tombol aksi: Create employee

Tombol **Create employee** di form Job Offer hanya tampil bila `doc.WorkflowState == "Accepted" && (user.HasRole("HR") || user.IsAdmin)` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Aksi "Create employee" (Workday Hire): dari penawaran Accepted dibuat Employee (data pelamar + jabatan penawaran), lalu Salary Assignment gaji pokok bila komponennya diatur di Payroll Settings, dan pelamar ditandai Hired. Employee dibuat sebagai user HR (izin berlaku).

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
- Payroll: lihat `_shared-Payroll.md`
