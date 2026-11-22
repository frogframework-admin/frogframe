<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Overtime Request (OvertimeRequest)

Aturan yang dijalankan kode saat dokumen Overtime Request (OvertimeRequest) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **EssApprovalNotifier**: Inbox notifications for self-service requests: the next approver (supervisor, project approver or the HR role) when a request reaches their stage, and the requester when it is decided. The ESS app shows them and links to the approval.
- **OvertimeRequestHook**: Overtime request: paid hours computed using PP 35/2021 multipliers; resolves day type &amp; work week from EmployeeDailySchedule when available.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `overtime_date`: "The date is required" — terjadi bila `d.Date("overtime_date") tidak ditemukan/kosong` (OvertimeRequestHook, TimeHooks.cs:213)
- `day_type`: "'Public holiday on the shortest working day' only applies to a 6-day work week" — terjadi bila `dayType == Overtime.ShortestDay && week != 6` (OvertimeRequestHook, TimeHooks.cs:240)
- `hours`: "At most {max} overtime hours on a {dayType.ToLowerInvariant()} ({week}-day work week)" — terjadi bila `!(dayType == Overtime.ShortestDay && week != 6) && hours > max` (OvertimeRequestHook, TimeHooks.cs:243)
- `hours`: "Overtime would total {others + hours} hours in the week of {D.Iso(monday)}; the limit is {Overtime.MaxPerWeek} hours" — terjadi bila `!(dayType == Overtime.ShortestDay && week != 6) && others + hours > Overtime.MaxPerWeek` (OvertimeRequestHook, TimeHooks.cs:252)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
- Overtime: lihat `_shared-Overtime.md`
