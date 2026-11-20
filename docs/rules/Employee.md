<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Employee (Employee)

Aturan yang dijalankan kode saat dokumen Employee (Employee) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **EmployeeHook**: Employee. Position model: unit, job profile, grade, location, and cost center are derived from the position; one active holder per position. Supervisor is derived from position/unit hierarchy; default leave &amp; expense approvers = supervisor.
- **EssCacheInvalidationHook**: Invalidates cached ESS profile, site, and policy data immediately when HR updates master documents.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `status`: "The Left status is applied through Employee Separation" — terjadi bila `sep is null && !c.User.IsAdmin` (EmployeeHook, HrHooks.cs:387)
- `position`: "Position {pos.Name} is {pos.Data.Str("status")}" — terjadi bila `pos.Data.Str("status") is "Closed" or "Frozen"` (EmployeeHook, HrHooks.cs:398)
- `position`: "Position {pos.Name} is already filled by {holder.Name}" — terjadi bila `await Hr.HolderAsync(m, pos.Name, me, ct) is { } holder && holder.Data.Str("position") == pos.Name` (EmployeeHook, HrHooks.cs:400)
- `probation_end_date`: "The probation end date is before the date of joining" — terjadi bila `d.Date("probation_end_date") is { } pe && d.Date("hire_date") is { } hd && pe < hd` (EmployeeHook, HrHooks.cs:423)
- `reports_to`: "An employee cannot report to themselves" — terjadi bila `d.Str("reports_to") == me` (EmployeeHook, HrHooks.cs:424)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- EssAttendanceService: lihat `_shared-EssAttendanceService.md`
- Masters: lihat `_shared-Masters.md`
