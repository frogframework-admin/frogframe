<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Shift Override (ShiftOverride)

Aturan yang dijalankan kode saat dokumen Shift Override (ShiftOverride) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **ShiftOverrideHook**: Shift Override: single-day schedule override validated against Workforce Schedule Rules and materialized into EmployeeDailySchedule.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `override_date`: "The override date is required" — terjadi bila `d.Date("override_date") tidak ditemukan/kosong` (ShiftOverrideHook, WsmHooks.cs:772)
- `new_shift_type`: "New shift type is required when schedule type is WORK" — terjadi bila `newType == "WORK" && newShift is null` (ShiftOverrideHook, WsmHooks.cs:779)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
- Overtime: lihat `_shared-Overtime.md`
- WsmLeaveSync: lihat `_shared-WsmLeaveSync.md`
