<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama WsmLeaveSync

Dipakai oleh: LeaveApplication, ShiftOverride, ShiftSwapRequest.

Synchronizes LeaveApplication and ShiftOverride/Swap updates with EmployeeDailySchedule and evaluates ShiftRequirement coverage.

## Peringatan

- "Manpower shortage alert on {D.Iso(gap.WorkDate)}: {gap.OrganizationUnit} / {gap.ShiftType} has {gap.ActualHeadcount} scheduled vs {gap.MinHeadcount} required." (WsmLeaveSync, WsmHooks.cs:1087)
