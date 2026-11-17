<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Withholding Tax Certificate (WithholdingCertificate)

Aturan yang dijalankan kode saat dokumen Withholding Tax Certificate (WithholdingCertificate) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **WithholdingCertificateHook**: Bukti potong: kode PPh, dasar, pihak, dan masa pajak dari dokumen sumber bila diisi.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `reference_name`: "{refName} is not submitted or has been cancelled" — terjadi bila `src.DocStatus != DocStatus.Submitted` (WithholdingCertificateHook, TaxBankHooks.cs:61)
- `tax_code`: "{code.Name} bukan kode PPh potong/pungut" — terjadi bila `code is not null && !code.Data.Bool("is_withholding")` (WithholdingCertificateHook, TaxBankHooks.cs:77)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
