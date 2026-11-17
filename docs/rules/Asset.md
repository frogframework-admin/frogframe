<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Fixed Asset (Asset)

Aturan yang dijalankan kode saat dokumen Fixed Asset (Asset) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Validasi (dokumen ditolak): Saat cancel

- `name`: "The asset has been depreciated by {posted[0].Run}; cancel that depreciation run first" — terjadi bila `posted.Count > 0` (AssetHook, AssetHooks.cs:119)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `depreciation_start_date`: "The useful life of {life} months has ended before the depreciation start date" — terjadi bila `!(available is null || start is null || life <= 0) && remaining <= 0` (AssetHook, AssetHooks.cs:96)
- `opening_accumulated_depreciation`: "Opening accumulated depreciation exceeds the depreciable amount" — terjadi bila `!(available is null || start is null || life <= 0) && gross - salvage - opening < 0` (AssetHook, AssetHooks.cs:97)
- `purchase_invoice`: "{pi} is not submitted" — terjadi bila `!(available is null || start is null || life <= 0) && d.Str("purchase_invoice") is { } pi && (await m.GetAsync("PurchaseInvoice", pi, ct))?.DocStatus != DocStatus.Submitted` (AssetHook, AssetHooks.cs:99)

## Validasi (dokumen ditolak): Setelah simpan/submit (posting, efek ke dokumen lain)

- `schedule`: "The depreciation schedule is empty" — terjadi bila `c.Doc.Data.Rows("schedule").Count == 0` (AssetHook, AssetHooks.cs:108)

## Peringatan (dokumen tetap tersimpan): Setelah simpan/submit (posting, efek ke dokumen lain)

- "This asset is not linked to a purchase invoice; make sure its acquisition has been booked to the fixed asset account." — terjadi bila `!(c.Doc.Data.Rows("schedule").Count == 0) && c.Doc.Data.Str("purchase_invoice") is null && !c.Doc.Data.Bool("is_existing_asset")` (AssetHook, AssetHooks.cs:111)

## Aturan bersama yang juga berlaku

- AssetBook: lihat `_shared-AssetBook.md`
- Masters: lihat `_shared-Masters.md`
