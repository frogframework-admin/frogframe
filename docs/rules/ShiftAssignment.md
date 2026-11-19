<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Shift Assignment (ShiftAssignment)

Aturan yang dijalankan kode saat dokumen Shift Assignment (ShiftAssignment) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **ShiftAssignmentHook**: Shift Assignment: links a Shift Pattern to a Shift Group, Employee, or Organization Unit with effective dates and cycle offset.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `start_date`: "Overlaps with active shift assignment {other.Name} for {targetVal}" — terjadi bila `Hr.Overlaps(start, end, other.Data.Date("start_date"), other.Data.Date("end_date"))` (ShiftAssignmentHook, WsmHooks.cs:203)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
