<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama WsmOptimizationService

Dipakai oleh: ScheduleOptimization.

Orchestrates loading DocFrame records, calling WsmOptimizerEngine, and applying proposals to EmployeeDailySchedule.

## Validasi (dokumen ditolak)

- `company`: "Company is required" — terjadi bila `d.Str("company") tidak ditemukan/kosong` (WsmOptimizationService, WsmOptimizer.cs:574)
- `from_date`: "From date is required" — terjadi bila `d.Date("from_date") tidak ditemukan/kosong` (WsmOptimizationService, WsmOptimizer.cs:575)
- `to_date`: "To date is required" — terjadi bila `d.Date("to_date") tidak ditemukan/kosong` (WsmOptimizationService, WsmOptimizer.cs:576)
- `status`: "Only a Proposed optimization can be applied" — terjadi bila `optDoc.Data.Str("status") != "Proposed"` (WsmOptimizationService, WsmOptimizer.cs:770)
