<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama Overtime

Dipakai oleh: Attendance, OvertimeRequest, Payslip.

Lembur menurut PP 35/2021: pengali (Pasal 31), batas jam (Pasal 26 jo. UU 13/2003 Pasal 78 sebagaimana diubah), upah sejam 1/173 (Pasal 32).

## Validasi (dokumen ditolak)

- `{path}`: "Payslip {paid.Name} for {period} is already submitted; this month is locked" — terjadi bila `!(employee is null) && paid is not null` (Overtime, TimeHooks.cs:64)
