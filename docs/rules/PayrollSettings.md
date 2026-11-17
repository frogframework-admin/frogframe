<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Payroll Settings (PayrollSettings)

Aturan yang dijalankan kode saat dokumen Payroll Settings (PayrollSettings) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **PayrollSettingsHook**: Payroll Settings: tabel PTKP, Pasal 17, dan TER diisi otomatis bila kosong; tiap kategori TER wajib punya baris tanpa batas.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `ter_rates`: "TER category {cat} needs a last row without an upper limit" — terjadi bila `!d.Rows("ter_rates").Any(r => r.Str("ter_category") == cat && r.Dec("up_to") == 0)` (PayrollSettingsHook, PayrollHooks.cs:117)
- `tax_brackets`: "Article 17 rates need a last row without an upper limit" — terjadi bila `!d.Rows("tax_brackets").Any(r => r.Dec("up_to") == 0)` (PayrollSettingsHook, PayrollHooks.cs:118)
- `ptkp_rates`: "PTKP status {s.Key} appears more than once" (PayrollSettingsHook, PayrollHooks.cs:120)
