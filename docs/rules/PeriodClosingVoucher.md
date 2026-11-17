<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Period Closing Voucher (PeriodClosingVoucher)

Aturan yang dijalankan kode saat dokumen Period Closing Voucher (PeriodClosingVoucher) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **PeriodClosingVoucherHook**: Tutup buku tahunan: saldo semua akun Laba Rugi tahun buku itu dinolkan ke akun laba ditahan; laba bersih dicatat di dokumen; tahun buku ditandai Ditutup (lewat event). Satu PCV aktif per tahun buku.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `fiscal_year`: "Fiscal year {fy.Name} is already closed" — terjadi bila `fy.Data.Str("status") == "Closed"` (PeriodClosingVoucherHook, AccountingExtraHooks.cs:58)
- `posting_date`: "The posting date must be the last day of the fiscal year ({fy.Data.Str("end_date")})" — terjadi bila `d.Date("posting_date") != fy.Data.Date("end_date")` (PeriodClosingVoucherHook, AccountingExtraHooks.cs:61)
- `fiscal_year`: "Fiscal year {fy.Name} was already closed by {dup.Name}" — terjadi bila `dup is not null` (PeriodClosingVoucherHook, AccountingExtraHooks.cs:64)

## Validasi (dokumen ditolak): Setelah simpan/submit (posting, efek ke dokumen lain)

- `closing_account`: "The retained earnings account is required" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft)` (PeriodClosingVoucherHook, AccountingExtraHooks.cs:76)
- `name`: "The closing entry is out of balance" — terjadi bila `!(c.Doc.DocStatus != DocStatus.Submitted || c.Previous?.DocStatus != DocStatus.Draft) && !(acc?.Data.Str("report_type") != "Profit and Loss") && posting.Difference != 0` (PeriodClosingVoucherHook, AccountingExtraHooks.cs:91)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
