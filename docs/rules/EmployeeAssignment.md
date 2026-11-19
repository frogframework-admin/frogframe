<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Employee Assignment (EmployeeAssignment)

Aturan yang dijalankan kode saat dokumen Employee Assignment (EmployeeAssignment) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **EmployeeAssignmentHook**: Employee Assignment: secondary placements and acting appointments (Plt./Pj.) with effective dates.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `position`: "Position {pos.Name} is {pos.Data.Str("status")}" — terjadi bila `pos.Data.Str("status") is "Closed" or "Frozen"` (EmployeeAssignmentHook, HrHooks.cs:567)
- `end_date`: "The end date must be on or after the start date" — terjadi bila `start is not null && end is not null && end < start` (EmployeeAssignmentHook, HrHooks.cs:575)
- `start_date`: "Overlaps with active assignment {other.Name}" — terjadi bila `sameSlot && Hr.Overlaps(start, end, other.Data.Date("start_date"), other.Data.Date("end_date"))` (EmployeeAssignmentHook, HrHooks.cs:587)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
