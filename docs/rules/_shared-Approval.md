<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama Approval

Dipakai oleh: Attendance, Employee, EmployeeAdvance, EmployeeAssignment, EmployeeDailySchedule, EmployeeSeparation, EmploymentContract, ExpenseClaim, JobChange, LeaveAllocation, LeaveApplication, OrganizationUnit, OvertimeRequest, PaymentEntry, PaymentRun, PayrollRun, Payslip, Position, PurchaseOrder, PurchaseRequest, ScheduleGeneration, ScheduleOptimization, ShiftAssignment, ShiftGroup, ShiftOverride, ShiftPattern, ShiftPreference, ShiftSwapRequest.

## Validasi (dokumen ditolak)

- `employee`: "{emp.Name} has left" — terjadi bila `emp.Data.Str("status") == "Left"` (Approval, EmployeeFinanceHooks.cs:19)
