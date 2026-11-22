<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Payroll Run (PayrollRun)

Aturan yang dijalankan kode saat dokumen Payroll Run (PayrollRun) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **PayrollRunHook**: Payroll Run: total dari payslip yang sudah submit; satu run aktif per (perusahaan, periode, unit). Submit: semua payslip run harus sudah submit, lalu satu jurnal: Dr beban gaji per komponen & cost center, Dr beban BPJS pemberi kerja; Cr utang gaji per karyawan (against = run, dibayar lewat Payment Entry), Cr utang PPh 21 (setelah DTP), Cr utang BPJS TK & Kesehatan, Cr akun komponen potongan (pihak = karyawan).
- **PayrollRunCreatePayslipsAction**: Aksi "Create payslips": draft payslip untuk setiap karyawan aktif perusahaan (dan unit) yang punya penugasan gaji. Idempoten.
- **PayrollRunSubmitPayslipsAction**: Aksi "Submit payslips": submit semua payslip draft run ini sebelum run di-submit.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `period`: "{dup.Name} already covers this company, period, and unit" — terjadi bila `dup is not null` (PayrollRunHook, PayrollHooks.cs:428)

## Validasi (dokumen ditolak): Setelah simpan/submit (posting, efek ke dokumen lain)

- `name`: "{all.Count(s => s.DocStatus == DocStatus.Draft)} payslips are still draft; submit or delete them first" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && all.Any(s => s.DocStatus == DocStatus.Draft)` (PayrollRunHook, PayrollHooks.cs:448)
- `name`: "There are no submitted payslips in this payroll run" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && all.Count == 0` (PayrollRunHook, PayrollHooks.cs:449)
- `posting_date`: "The posting date is required" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && !(all.Count == 0)` (PayrollRunHook, PayrollHooks.cs:451)
- `name`: "Set '{label}' in Payroll Settings" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && !(all.Count == 0)` (PayrollRunHook, PayrollHooks.cs:454)
- `name`: "Payroll entry is out of balance by {posting.Difference}" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && !(all.Count == 0) && posting.Difference != 0` (PayrollRunHook, PayrollHooks.cs:475)

## Tombol aksi gagal: Saat tombol aksi diklik

- "Set the period first" — terjadi bila `period is null` (PayrollRunCreatePayslipsAction, PayrollHooks.cs:499)
- "{msg} Failed: {string.Join("; ", failed.Take(10))}" — terjadi bila `!(period is null)` (PayrollRunCreatePayslipsAction, PayrollHooks.cs:537)
- "{msg} Failed: {string.Join("; ", failed.Take(10))}" (PayrollRunSubmitPayslipsAction, PayrollHooks.cs:566)

## Tombol aksi: Create payslips

Tombol **Create payslips** di form Payroll Run hanya tampil bila `doc.DocStatus == DocStatus.Draft && user.HasRole(ErpRoles.HrPayroll)` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Aksi "Create payslips": draft payslip untuk setiap karyawan aktif perusahaan (dan unit) yang punya penugasan gaji. Idempoten.

## Tombol aksi: Submit payslips

Tombol **Submit payslips** di form Payroll Run hanya tampil bila `doc.DocStatus == DocStatus.Draft && user.HasRole(ErpRoles.HrPayroll)` (WorkflowState = state workflow, DocStatus = Draft/Submitted, HasRole = peran user). Aksi "Submit payslips": submit semua payslip draft run ini sebelum run di-submit.

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- BudgetControl: lihat `_shared-BudgetControl.md`
- Masters: lihat `_shared-Masters.md`
- Payroll: lihat `_shared-Payroll.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
