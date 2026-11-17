<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama Approval

Dipakai oleh: Attendance, EmployeeAdvance, EmployeeSeparation, EmploymentContract, ExpenseClaim, JobChange, LeaveAllocation, LeaveApplication, OvertimeRequest, PaymentEntry, PaymentRun, Payslip, PurchaseOrder, PurchaseRequest.

## Validasi (dokumen ditolak)

- `employee`: "{emp.Name} has left" — terjadi bila `emp.Data.Str("status") == "Left"` (Approval, EmployeeFinanceHooks.cs:19)
