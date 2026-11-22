<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Leave Application (LeaveApplication)

Aturan yang dijalankan kode saat dokumen Leave Application (LeaveApplication) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **EssApprovalNotifier**: Inbox notifications for self-service requests: the next approver (supervisor, project approver or the HR role) when a request reaches their stage, and the requester when it is decided. The ESS app shows them and links to the approval.
- **LeaveApplicationHook**: Leave application: calculated &amp; validated on save and before approval transition. When approved, updates materialized EmployeeDailySchedule rows to LEAVE and checks shift manpower coverage.

## Validasi (dokumen ditolak): Pemeriksaan CheckAsync

- `leave_type`: "{type.Name} is only for {gender.ToLowerInvariant()} employees" — terjadi bila `!(from is null || to is null || to < from) && gender is "Female" or "Male" && emp.Data.Str("gender") != gender` (LeaveApplicationHook, HrHooks.cs:948)
- `from_date`: "There are no working days in this date range" — terjadi bila `!(from is null || to is null || to < from) && days <= 0` (LeaveApplicationHook, HrHooks.cs:951)
- `to_date`: "At most {max} consecutive days for {type.Name}" — terjadi bila `!(from is null || to is null || to < from) && max > 0 && days > max` (LeaveApplicationHook, HrHooks.cs:953)
- `from_date`: "Overlaps with application {clash.Name}" — terjadi bila `!(from is null || to is null || to < from) && mine.FirstOrDefault(a => Hr.Overlaps(from, to, a.Data.Date("from_date"), a.Data.Date("to_date"))) is { } clash` (LeaveApplicationHook, HrHooks.cs:959)
- `leave_type`: "There is no {type.Name} allocation for this period" — terjadi bila `alloc is null` (LeaveApplicationHook, HrHooks.cs:965)
- `to_date`: "Only {balance} days of {type.Name} remain" — terjadi bila `days > balance` (LeaveApplicationHook, HrHooks.cs:974)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
- WsmLeaveSync: lihat `_shared-WsmLeaveSync.md`
