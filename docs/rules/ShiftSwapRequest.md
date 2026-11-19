<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Shift Swap Request (ShiftSwapRequest)

Aturan yang dijalankan kode saat dokumen Shift Swap Request (ShiftSwapRequest) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **ShiftSwapRequestHook**: Shift Swap Request: two-party shift exchange with peer acceptance, manager approval, and Workforce Constraint Engine validation for both parties. Updates both employees' EmployeeDailySchedule records when approved.

## Validasi (dokumen ditolak): Pemeriksaan ValidateSwapAsync

- `target_employee`: "An employee cannot swap shifts with themselves" — terjadi bila `reqEmp.Name == tgtEmp.Name` (ShiftSwapRequestHook, WsmHooks.cs:829)
- `swap_date`: "No materialized daily schedule exists for {reqEmp.Name} on {D.Iso(swapDate.Value)}" — terjadi bila `reqSched is null` (ShiftSwapRequestHook, WsmHooks.cs:848)
- `target_swap_date`: "No materialized daily schedule exists for {tgtEmp.Name} on {D.Iso(targetDate)}" — terjadi bila `tgtSched is null` (ShiftSwapRequestHook, WsmHooks.cs:850)
- `swap_date`: "Cannot swap a day that is marked as approved leave" — terjadi bila `reqType == "LEAVE" || tgtType == "LEAVE"` (ShiftSwapRequestHook, WsmHooks.cs:867)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
- Overtime: lihat `_shared-Overtime.md`
- WsmLeaveSync: lihat `_shared-WsmLeaveSync.md`
