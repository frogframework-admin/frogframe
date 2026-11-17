<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Tax Invoice (TaxInvoice)

Aturan yang dijalankan kode saat dokumen Tax Invoice (TaxInvoice) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **TaxInvoiceHook**: Faktur Pajak: nilai, pihak, dan masa pajak dari dokumen sumber; satu faktur pajak aktif per dokumen (kecuali pengganti).

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `reference_name`: "{refName} is not submitted or has been cancelled" — terjadi bila `!(refType is null || refName is null) && src.DocStatus != DocStatus.Submitted` (TaxInvoiceHook, TaxBankHooks.cs:18)
- `reference_name`: "{refName} already has tax invoice {others[0].Name}; create a replacement by filling 'Replacement for'" — terjadi bila `!(refType is null || refName is null) && others.Count > 0` (TaxInvoiceHook, TaxBankHooks.cs:35)

## Peringatan (dokumen tetap tersimpan): Saat simpan/submit (validasi)

- "The original invoice {origin} has no active tax invoice yet; fill 'Original tax invoice (return)' manually" (TaxInvoiceHook, TaxBankHooks.cs:44)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
