<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Employee Assignment (EmployeeAssignment)

Aturan yang dijalankan kode saat dokumen Employee Assignment (EmployeeAssignment) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **EmployeeAssignmentHook**: Employee Assignment: secondary placements and acting appointments (Plt./Pj.) with effective dates.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `position`: "Position {pos.Name} is {pos.Data.Str("status")}" — terjadi bila `pos.Data.Str("status") is "Closed" or "Frozen"` (EmployeeAssignmentHook, HrHooks.cs:685)
- `end_date`: "The end date must be on or after the start date" — terjadi bila `start is not null && end is not null && end < start` (EmployeeAssignmentHook, HrHooks.cs:693)
- `acting_for`: "An Acting (Plt.) assignment must specify either 'Acting for' or a 'Position'" — terjadi bila `asgType == "Acting" && d.Str("acting_for") is null && d.Str("position") is null` (EmployeeAssignmentHook, HrHooks.cs:698)
- `acting_for`: "An employee cannot be Acting for themselves" — terjadi bila `d.Str("acting_for") == emp.Name` (EmployeeAssignmentHook, HrHooks.cs:700)
- `dotted_line_manager`: "A Dotted-line assignment must specify either a 'Dotted-line manager' or a 'Project'" — terjadi bila `repLine == "Dotted" && d.Str("dotted_line_manager") is null && d.Str("project") is null` (EmployeeAssignmentHook, HrHooks.cs:702)
- `dotted_line_manager`: "An employee cannot be their own dotted-line manager" — terjadi bila `d.Str("dotted_line_manager") == emp.Name` (EmployeeAssignmentHook, HrHooks.cs:704)
- `start_date`: "Overlaps with active assignment {other.Name}" — terjadi bila `sameSlot && Hr.Overlaps(start, end, other.Data.Date("start_date"), other.Data.Date("end_date"))` (EmployeeAssignmentHook, HrHooks.cs:722)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
