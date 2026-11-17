<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Payslip (Payslip)

Aturan yang dijalankan kode saat dokumen Payslip (Payslip) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **PayslipHook**: Payslip: semua angka dihitung hook setiap simpan. - BPJS: dasar upah = komponen bertanda "BPJS base"; JP dibatasi batas upah JP, Kesehatan dibatasi plafon (dan minimal UMK bila diisi). - PPh 21 masa Januari–November: bruto (pendapatan kena pajak + JKK + JKM + BPJS Kesehatan pemberi kerja) × TER kategori PTKP. - Masa terakhir (Desember, atau bulan karyawan keluar): bruto setahun − biaya jabatan − JHT/JP pegawai − PTKP → PKP dibulatkan ke bawah ribuan → tarif Pasal 17 → dikurangi PPh 21 yang sudah dipotong (termasuk bagian DTP). Hasil negatif dikembalikan ke pegawai. Pegawai yang mulai bekerja di tengah tahun tidak disetahunkan (penghasilan riil setahun dari payslip yang ada). - DTP (PMK 105/2025): bila diaktifkan dan karyawan memenuhi syarat, PPh 21 masa itu dibayarkan tunai ke karyawan (tidak dipotong).

## Validasi (dokumen ditolak): Saat cancel

- `name`: "{run.Name} is submitted; cancel the payroll run first" — terjadi bila `run is not null && run.DocStatus == DocStatus.Submitted` (PayslipHook, PayrollHooks.cs:386)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `payroll_run`: "{run.Name} is already submitted; create a new payroll run" — terjadi bila `run.DocStatus != DocStatus.Draft && c.Doc.DocStatus == DocStatus.Draft && c.Previous is null` (PayslipHook, PayrollHooks.cs:202)
- `employee`: "{emp.Name} belongs to another company" — terjadi bila `emp.Data.Str("company") != run.Data.Str("company")` (PayslipHook, PayrollHooks.cs:209)
- `employee`: "{emp.Name} already has payslip {others[0].Name} for {period}" — terjadi bila `others.Count > 0` (PayslipHook, PayrollHooks.cs:214)
- `employee`: "{emp.Name} has no active salary assignment valid on {D.Iso(periodEnd)}" — terjadi bila `assignment is null` (PayslipHook, PayrollHooks.cs:217)
- `{table}[{i}].salary_component`: "{comp.Name} is not a {type.ToLowerInvariant()}" — terjadi bila `comp.Data.Str("component_type") != type` (PayslipHook, PayrollHooks.cs:247)
- `{table}[{i}].salary_component`: "Set the GL account of deduction component {comp.Name}" — terjadi bila `comp.Data.Str("account") is null` (PayslipHook, PayrollHooks.cs:261)
- `employee`: "Set the PTKP status of {emp.Name} (Employee, Identity & Tax)" — terjadi bila `!(comp is null)` (PayslipHook, PayrollHooks.cs:294)

## Peringatan (dokumen tetap tersimpan): Saat simpan/submit (validasi)

- "Overtime: {ot.WeightedHours} paid hours × {ot.HourlyWage} = {ot.Amount}" (PayslipHook, PayrollHooks.cs:235)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
- Overtime: lihat `_shared-Overtime.md`
- Payroll: lihat `_shared-Payroll.md`
