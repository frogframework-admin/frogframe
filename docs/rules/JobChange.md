<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Job Change (JobChange)

Aturan yang dijalankan kode saat dokumen Job Change (JobChange) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **JobChangeHook**: Perubahan jabatan (Workday Change Job): diterapkan ke Employee saat tanggal efektif (langsung bila sudah lewat, else job harian).

## Validasi (dokumen ditolak): Saat cancel

- `name`: "This change has already been applied to the employee; create a new Job Change to reverse it" — terjadi bila `emp?.Data.Str("last_job_change") == c.Doc.Name` (JobChangeHook, HrHooks.cs:180)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `effective_date`: "The effective date is before the employee's date of joining" — terjadi bila `d.Date("effective_date") is { } eff && emp.Data.Date("hire_date") is { } hire && eff < hire` (JobChangeHook, HrHooks.cs:155)
- `new_position`: "Position {np} is {pos.Data.Str("status")}" — terjadi bila `pos.Data.Str("status") is "Closed" or "Frozen"` (JobChangeHook, HrHooks.cs:159)
- `new_position`: "Position {np} is still filled by {holder.Name}" — terjadi bila `await Hr.HolderAsync(m, np, emp.Name, ct) is { } holder` (JobChangeHook, HrHooks.cs:160)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
