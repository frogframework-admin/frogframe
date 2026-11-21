<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Schedule Optimization (ScheduleOptimization)

Aturan yang dijalankan kode saat dokumen Schedule Optimization (ScheduleOptimization) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **ScheduleOptimizationHook**: Schedule Optimization validation hook.
- **ScheduleOptimizationRunAction**: Form action to run the OR-Tools CP-SAT optimizer on a ScheduleOptimization document.
- **ScheduleOptimizationApplyAction**: Form action to apply a Proposed ScheduleOptimization to EmployeeDailySchedule.
- **ScheduleOptimizationDiscardAction**: Form action to discard a Proposed ScheduleOptimization.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `to_date`: "The end date must be on or after the start date" — terjadi bila `!(from is null || to is null) && to < from` (ScheduleOptimizationHook, WsmOptimizer.cs:1146)
- `to_date`: "A single optimization run cannot exceed {MaxOptimizationDays} days (requested {span} days)" — terjadi bila `!(from is null || to is null) && !(to < from) && span > MaxOptimizationDays` (ScheduleOptimizationHook, WsmOptimizer.cs:1149)

## Tombol aksi: Run Optimizer (CP-SAT)

Tombol **Run Optimizer (CP-SAT)** di form Schedule Optimization hanya tampil bila `(user.HasRole(ErpRoles.HrTimeAttendance) || user.HasRole(ErpRoles.Manager)) && doc.Data.Str("status") is null or "Draft" or "Proposed" or "Failed"` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Form action to run the OR-Tools CP-SAT optimizer on a ScheduleOptimization document.

## Tombol aksi: Apply Proposal

Tombol **Apply Proposal** di form Schedule Optimization hanya tampil bila `user.HasRole(ErpRoles.HrTimeAttendance) && doc.Data.Str("status") == "Proposed"` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Form action to apply a Proposed ScheduleOptimization to EmployeeDailySchedule.

## Tombol aksi: Discard Proposal

Tombol **Discard Proposal** di form Schedule Optimization hanya tampil bila `(user.HasRole(ErpRoles.HrTimeAttendance) || user.HasRole(ErpRoles.Manager)) && doc.Data.Str("status") == "Proposed"` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Form action to discard a Proposed ScheduleOptimization.

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
- Overtime: lihat `_shared-Overtime.md`
- WsmOptimizationService: lihat `_shared-WsmOptimizationService.md`
