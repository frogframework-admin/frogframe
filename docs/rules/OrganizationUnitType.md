<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Organization Unit Type (OrganizationUnitType)

Aturan yang dijalankan kode saat dokumen Organization Unit Type (OrganizationUnitType) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **OrganizationUnitTypeHook**: Organization unit type: dynamic hierarchy level definition; system-seeded types cannot be deleted.

## Validasi (dokumen ditolak): Saat hapus

- `name`: "System organization unit types cannot be deleted" — terjadi bila `c.Doc.Data.Bool("is_system")` (OrganizationUnitTypeHook, HrHooks.cs:359)

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `parent_type`: "An organization unit type cannot be its own parent type" — terjadi bila `d.Str("parent_type") == name` (OrganizationUnitTypeHook, HrHooks.cs:347)
- `type_code`: "Type code {code} is already used by {dup.Name}" — terjadi bila `existing.FirstOrDefault(x => x.Name != name && x.Name != c.Doc.Name) is { } dup` (OrganizationUnitTypeHook, HrHooks.cs:351)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
