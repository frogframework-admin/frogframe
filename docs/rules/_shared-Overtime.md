<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama Overtime

Dipakai oleh: Attendance, EmployeeDailySchedule, OvertimeRequest, Payslip, ShiftOverride, ShiftSwapRequest.

Overtime calculation under PP 35/2021: tier multipliers (Art. 31), hour caps (Art. 26), and hourly wage 1/173 (Art. 32).

## Validasi (dokumen ditolak)

- `{path}`: "Payslip {paid.Name} for {period} is already submitted; this month is locked" — terjadi bila `!(employee is null) && paid is not null` (Overtime, TimeHooks.cs:70)
