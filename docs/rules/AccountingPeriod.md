<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Accounting Period (AccountingPeriod)

Aturan yang dijalankan kode saat dokumen Accounting Period (AccountingPeriod) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **AccountingPeriodRulesHook**: Periode: tidak tumpang tindih, berada di dalam tahun bukunya, jejak penutupan dicatat, dan membuka kembali periode yang sudah ditutup hanya oleh System Manager.

## Validasi (dokumen ditolak): Pemeriksaan CheckAsync

- `start_date`: "The period must fall within fiscal year {fy.Name}" — terjadi bila `!(start is null || end is null) && await m.GetAsync("FiscalYear", d.Str("fiscal_year"), ct) is { } fy && (start < fy.Data.Date("start_date") || end > fy.Data.Date("end_date"))` (AccountingPeriodRulesHook, AccountingHooks.cs:90)
- `start_date`: "The period overlaps with {p.Name}" — terjadi bila `!(p.Name == c.Doc.Name) && start <= p.Data.Date("end_date") && p.Data.Date("start_date") <= end` (AccountingPeriodRulesHook, AccountingHooks.cs:96)
- `status`: "Only a System Manager can reopen a closed period" — terjadi bila `!c.User.IsAdmin` (AccountingPeriodRulesHook, AccountingHooks.cs:108)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
