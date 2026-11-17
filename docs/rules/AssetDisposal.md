<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Asset Disposal (AssetDisposal)

Aturan yang dijalankan kode saat dokumen Asset Disposal (AssetDisposal) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **AssetDisposalHook**: Pelepasan aset: Dr akumulasi + Dr laba/rugi pelepasan (nilai buku) / Cr harga perolehan. Hasil penjualan dicatat lewat Faktur Penjualan dengan akun pendapatan = akun laba/rugi pelepasan kategori, sehingga saldo akun itu = harga jual − nilai buku.

## Validasi (dokumen ditolak): Pemeriksaan ValueAsync

- `asset`: "Asset {asset.Name} is not active" — terjadi bila `asset.DocStatus != DocStatus.Submitted` (AssetDisposalHook, AssetHooks.cs:216)
- `asset`: "Asset {asset.Name} was already disposed by {other.Name}" — terjadi bila `await AssetBook.DisposalAsync(m, asset.Name, ct) is { } other && other.Name != c.Doc.Name` (AssetDisposalHook, AssetHooks.cs:218)
- `disposal_date`: "The disposal date is required" — terjadi bila `d.Date("disposal_date") tidak ditemukan/kosong` (AssetDisposalHook, AssetHooks.cs:219)
- `disposal_date`: "Depreciation for {missing.Str("schedule_date")} is not posted; run a Depreciation Run up to the disposal date" — terjadi bila `missing is not null` (AssetDisposalHook, AssetHooks.cs:224)

## Aturan bersama yang juga berlaku

- AssetBook: lihat `_shared-AssetBook.md`
- Masters: lihat `_shared-Masters.md`
- Periods: lihat `_shared-Periods.md`
- Posting: lihat `_shared-Posting.md`
