<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Salary Assignment (SalaryAssignment)

Aturan yang dijalankan kode saat dokumen Salary Assignment (SalaryAssignment) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **SalaryAssignmentHook**: Penugasan gaji: komponen di tabel pendapatan harus Earning, di tabel potongan harus Deduction; satu per (karyawan, tanggal berlaku).

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `{table}[{i}].salary_component`: "{rows[i].Str("salary_component")} is a {t.ToLowerInvariant()}, not a {type.ToLowerInvariant()}" — terjadi bila `await m.StrAsync("SalaryComponent", rows[i].Str("salary_component"), "component_type", ct) is { } t && t != type` (SalaryAssignmentHook, PayrollHooks.cs:139)
- `from_date`: "{dup.Name} already starts on this date for this employee" — terjadi bila `dup is not null` (SalaryAssignmentHook, PayrollHooks.cs:143)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
