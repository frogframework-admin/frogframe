<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Shift Group (ShiftGroup)

Aturan yang dijalankan kode saat dokumen Shift Group (ShiftGroup) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **ShiftGroupHook**: Shift Group: operational work group/squad with effective-dated member assignments.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `members[{i}].effective_to`: "The effective end date must be on or after the effective start date" — terjadi bila `from is not null && to is not null && to < from` (ShiftGroupHook, WsmHooks.cs:66)
- `members[{i}].employee`: "Employee {emp} has overlapping active membership in this group" — terjadi bila `Hr.Overlaps(from, to, prev.Date("effective_from"), prev.Date("effective_to"))` (ShiftGroupHook, WsmHooks.cs:75)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
