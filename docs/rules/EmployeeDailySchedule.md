<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Employee Daily Schedule (EmployeeDailySchedule)

Aturan yang dijalankan kode saat dokumen Employee Daily Schedule (EmployeeDailySchedule) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **EmployeeDailyScheduleHook**: Employee Daily Schedule: materialized daily schedule slot per employee and work date; computes cross-midnight datetime windows.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `work_date`: "The work date is required" — terjadi bila `d.Date("work_date") tidak ditemukan/kosong` (EmployeeDailyScheduleHook, WsmHooks.cs:220)
- `work_date`: "{emp.Name} already has daily schedule {dup.Name} on {D.Iso(date)}" — terjadi bila `dup is not null` (EmployeeDailyScheduleHook, WsmHooks.cs:227)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
- Overtime: lihat `_shared-Overtime.md`
