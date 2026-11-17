<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bersama AssetBook

Dipakai oleh: Asset, AssetDisposal, DepreciationRun, EmploymentContract.

Buku aset. Aset yang sudah di-submit tidak bisa diubah, jadi "sudah disusutkan" tidak disimpan di Asset: sumbernya baris DepreciationRun yang sudah di-submit (dan AssetDisposal untuk status dilepas).

## Validasi (dokumen ditolak)

- `{path}`: "{label} is not set on category {cat.Name}" — terjadi bila `> cat.Data.Str(f) tidak ditemukan/kosong` (AssetBook, AssetHooks.cs:70)
