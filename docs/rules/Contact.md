<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Contact (Contact)

Aturan yang dijalankan kode saat dokumen Contact (Contact) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **PartyPrimaryHook**: Hanya satu Address/Contact "utama" per pihak yang ditautkan (child PartyLink).

## Validasi (dokumen ditolak): Pemeriksaan CheckAsync

- `is_primary`: "{r.Str("link_name")} already has a primary {c.Meta.Def.Title.ToLowerInvariant()}: {o.Name}" — terjadi bila `!(!c.Doc.Data.Bool("is_primary")) && mine.Contains((r.Str("link_doctype"), r.Str("link_name")))` (PartyPrimaryHook, AccountingExtraHooks.cs:38)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
