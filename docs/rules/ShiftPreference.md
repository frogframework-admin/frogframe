<!-- Dihasilkan otomatis oleh tools/generate_ai_rules.py dari kode hook. Jangan diedit manual; jalankan ulang skripnya. -->

# Aturan bisnis Shift Preference (ShiftPreference)

Aturan yang dijalankan kode saat dokumen Shift Preference (ShiftPreference) disimpan, di-submit, dibatalkan, atau diproses. Gunakan untuk menjelaskan kenapa dokumen ditolak, kenapa muncul peringatan, atau kenapa tombol tidak tampil.

## Ringkasan perilaku

- **ShiftPreferenceHook**: Shift Preference validation hook.

## Validasi (dokumen ditolak): Saat simpan/submit (validasi)

- `to_date`: "The end date must be on or after the start date" — terjadi bila `from is not null && to is not null && to < from` (ShiftPreferenceHook, WsmOptimizer.cs:913)

## Aturan bersama yang juga berlaku

- Approval: lihat `_shared-Approval.md`
- Masters: lihat `_shared-Masters.md`
