<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Attendance Correction (AttendanceCorrection)

Aturan yang dijalankan kode saat dokumen Attendance Correction (AttendanceCorrection) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **AttendanceCorrectionHook**: Attendance correction: resolves the approver and the recorded attendance, checks the requested times and the payroll lock; once approved the attendance is updated (AttendanceCorrectionApply) and its code recalculated.
- **EssApprovalNotifier**: Inbox notifications for self-service requests: the next approver (supervisor, project approver or the HR role) when a request reaches their stage, and the requester when it is decided. The ESS app shows them and links to the approval.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `attendance_date`: "The date is required" — terjadi bila `d.Date("attendance_date") tidak ditemukan/kosong` (AttendanceCorrectionHook, EssRequestHooks.cs:92)
- `attendance_date`: "Attendance can only be corrected for today or past days" — terjadi bila `date > today` (AttendanceCorrectionHook, EssRequestHooks.cs:94)
- `requested_check_in`: "Check-in must be on {D.Iso(date)}" — terjadi bila `!(date > today) && AttendanceHook.LocalTime(d.GetValueOrDefault("requested_check_in")) is { } ci && (ci < dayStart.AddHours(-6) || ci >= dayStart.AddDays(1))` (AttendanceCorrectionHook, EssRequestHooks.cs:115)
- `requested_check_out`: "Check-out must be on {D.Iso(date)} or the next morning" — terjadi bila `!(date > today) && AttendanceHook.LocalTime(d.GetValueOrDefault("requested_check_out")) is { } co && (co <= dayStart || co >= dayStart.AddDays(2))` (AttendanceCorrectionHook, EssRequestHooks.cs:117)
- `attendance_date`: "Correction {open.Name} for {D.Iso(date)} is still open" — terjadi bila `!(date > today) && open is not null` (AttendanceCorrectionHook, EssRequestHooks.cs:121)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
- Overtime: lihat `_shared-Overtime.md`
