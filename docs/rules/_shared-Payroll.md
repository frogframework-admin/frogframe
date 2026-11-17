<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama Payroll

Dipakai oleh: CommissionStatement, JobOffer, PaymentEntry, PayrollRun, Payslip.

## Validasi (dokumen ditolak)

- `name`: "Open Payroll Settings and save it once to load the PTKP, TER, and Article 17 tables" — terjadi bila `s.Rows("ter_rates").Count == 0 || s.Rows("ptkp_rates").Count == 0 || s.Rows("tax_brackets").Count == 0` (Payroll, PayrollHooks.cs:164)
