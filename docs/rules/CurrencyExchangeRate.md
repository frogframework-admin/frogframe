<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Currency Exchange Rate (CurrencyExchangeRate)

Aturan yang dijalankan kode saat dokumen Currency Exchange Rate (CurrencyExchangeRate) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **CurrencyExchangeRateRulesHook**: Kurs unik per (tanggal, jenis, dari, ke).

## Validasi (dokumen ditolak): Pemeriksaan CheckAsync

- `rate_date`: "The same exchange rate already exists in {dup.Name}" — terjadi bila `dup is not null` (CurrencyExchangeRateRulesHook, AccountingExtraHooks.cs:20)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
