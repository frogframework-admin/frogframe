<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Fiscal Year (FiscalYear)

Aturan yang dijalankan kode saat dokumen Fiscal Year (FiscalYear) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **FiscalYearRulesHook**: Tahun buku tidak tumpang tindih.

## Validasi (dokumen ditolak): Pemeriksaan CheckAsync

- `start_date`: "Overlaps with fiscal year {y.Name}" — terjadi bila `!(y.Name == c.Doc.Name) && start <= y.Data.Date("end_date") && y.Data.Date("start_date") <= end` (FiscalYearRulesHook, AccountingHooks.cs:67)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
