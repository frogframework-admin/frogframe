<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Customer Contract (CustomerContract)

Aturan yang dijalankan kode saat dokumen Customer Contract (CustomerContract) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **CustomerContractHook**: Kontrak pelanggan: perpanjangan harus pelanggan yang sama; status dihitung saat dibuka.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `renewal_of`: "{prev.Name} belongs to another customer" — terjadi bila `await m.GetAsync("CustomerContract", d.Str("renewal_of"), ct) is { } prev && prev.Data.Str("customer") != d.Str("customer")` (CustomerContractHook, CrmExtraHooks.cs:172)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
