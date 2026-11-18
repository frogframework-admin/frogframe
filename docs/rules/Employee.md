<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Employee (Employee)

Aturan yang dijalankan kode saat dokumen Employee (Employee) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **EmployeeHook**: Karyawan. Model Posisi: unit, profil jabatan, grade, lokasi, cost center diturunkan dari posisi; satu pemangku aktif per posisi. Atasan dari hierarki posisi/unit; approver cuti & klaim default = atasan. Keluar: tanggal keluar dari Employee Separation, posisi dikosongkan. Perubahan posisi/status disinkronkan ke Position lewat event (Position menghitung pemangkunya sendiri).
- **EssCacheInvalidationHook**: Invalidates cached ESS profile, site, and policy data immediately when HR updates master documents.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `status`: "The Left status is applied through Employee Separation" — terjadi bila `sep is null && !c.User.IsAdmin` (EmployeeHook, HrHooks.cs:60)
- `position`: "Position {pos.Name} is {pos.Data.Str("status")}" — terjadi bila `pos.Data.Str("status") is "Closed" or "Frozen"` (EmployeeHook, HrHooks.cs:71)
- `position`: "Position {pos.Name} is already filled by {holder.Name}" — terjadi bila `await Hr.HolderAsync(m, pos.Name, me, ct) is { } holder` (EmployeeHook, HrHooks.cs:72)
- `probation_end_date`: "The probation end date is before the date of joining" — terjadi bila `d.Date("probation_end_date") is { } pe && d.Date("hire_date") is { } hd && pe < hd` (EmployeeHook, HrHooks.cs:93)
- `reports_to`: "An employee cannot report to themselves" — terjadi bila `d.Str("reports_to") == me` (EmployeeHook, HrHooks.cs:94)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
