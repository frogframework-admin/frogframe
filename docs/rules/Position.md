<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Position (Position)

Aturan yang dijalankan kode saat dokumen Position (Position) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **PositionHook**: Position: holder &amp; status computed from Employee/EmployeeAssignment; default grade/cost center from job profile/unit.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `status`: "The position is still filled by {holder.Name}; move the employee first" — terjadi bila `holder is not null && status is "Closed" or "Frozen"` (PositionHook, HrHooks.cs:337)

## Aturan bersama yang juga berlaku

- Masters: lihat `_shared-Masters.md`
