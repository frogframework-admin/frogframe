<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Depreciation Run (DepreciationRun)

Aturan yang dijalankan kode saat dokumen Depreciation Run (DepreciationRun) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **DepreciationRunHook**: Satu dokumen per periode: memposting semua jadwal penyusutan yang jatuh tempo dan belum diposting.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `items`: "No depreciation is due up to {D.Iso(date)}" — terjadi bila `d.Rows("items").Count == 0` (DepreciationRunHook, AssetHooks.cs:170)

## Validasi (dokumen ditolak): Setelah simpan/submit (posting, efek ke dokumen lain)

- `items[{i}].asset`: "Asset {it.Str("asset")} is not active" — terjadi bila `asset is null || asset.DocStatus != DocStatus.Submitted` (DepreciationRunHook, AssetHooks.cs:190)
- `items[{i}].asset`: "Depreciation of {asset.Name} for {it.Str("schedule_date")} is already posted" — terjadi bila `posted.Contains((asset.Name, it.Str("schedule_date") ?? ""))` (DepreciationRunHook, AssetHooks.cs:191)

## Aturan bersama yang juga berlaku

- AssetBook: lihat `_shared-AssetBook.md`
- Masters: lihat `_shared-Masters.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
