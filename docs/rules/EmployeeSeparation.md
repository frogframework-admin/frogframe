<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Employee Separation (EmployeeSeparation)

Aturan yang dijalankan kode saat dokumen Employee Separation (EmployeeSeparation) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **EmployeeSeparationHook**: Employee Separation (Workday Terminate): default checklist; applied on the last working day (Employee → Left, user deactivated).

## Validasi (dokumen ditolak): Saat cancel

- `name`: "The separation has already been applied; reactivate the employee through HR (status) before cancelling" — terjadi bila `emp?.Data.Str("status") == Hr.Left` (EmployeeSeparationHook, HrHooks.cs:818)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `last_working_date`: "The last working date is before the date of joining" — terjadi bila `d.Date("last_working_date") is { } last && emp.Data.Date("hire_date") is { } hire && last < hire` (EmployeeSeparationHook, HrHooks.cs:798)
- `employee`: "Separation {other.Name} already exists" — terjadi bila `!(d.Date("last_working_date") is { } last && emp.Data.Date("hire_date") is { } hire && last < hire) && other is not null` (EmployeeSeparationHook, HrHooks.cs:801)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
